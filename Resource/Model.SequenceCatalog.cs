using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Sandbox.Mounting.Source1;

internal sealed partial class SourceModelBuilder
{
	/// <summary>Retains every original sequence and unique blend track without baking pose parameters or world-dependent IK into a fixed pose.</summary>
	internal static (PreparedAnimationClip[] Clips, SourceSequenceCatalog Catalog) PrepareSequenceCatalog(MdlFile mdl, string modelPath, Func<string, Stream> openFile)
	{
		var sourceFiles = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
		var originalOpen = openFile;
		long sourceBytes = 0;
		openFile = path =>
		{
			if (!sourceFiles.TryGetValue(path, out var bytes))
			{
				using var stream = originalOpen(path);
				if (stream.Length > 128L * 1024 * 1024 - sourceBytes)
					throw new NotSupportedException("Animation source files exceed the 128 MiB per-model input budget");
				bytes = new byte[checked((int)stream.Length)];
				stream.ReadExactly(bytes);
				sourceFiles.Add(path, bytes);
				sourceBytes += bytes.Length;
			}

			return new MemoryStream(bytes, false);
		};

		const long maximumPoseBytes = 256L * 1024 * 1024;
		var clips = new List<PreparedAnimationClip>();
		var entries = new List<SourceSequenceEntry>();
		var trackData = new List<SourceSequenceTrack>();
		var tracks = new Dictionary<(string Path, int Index), string>();
		var failures = new Dictionary<(string Path, int Index), string>();
		var animationNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		long allocated = 0;

		foreach (var included in mdl.ReadIncludedSequences(modelPath, openFile))
		{
			var sequence = included.Sequence;
			using var sequenceStream = openFile(included.ModelPath);
			var entry = new SourceSequenceEntry
			{
				Name = sequence.Name,
				SourceModelPath = included.ModelPath,
				SourceIndex = sequence.Index,
				Activity = sequence.Activity,
				Flags = sequence.Flags,
				GridWidth = sequence.GridWidth,
				GridHeight = sequence.GridHeight,
				ParameterX = sequence.ParameterX,
				ParameterY = sequence.ParameterY,
				KeysX = sequence.KeysX,
				KeysY = sequence.KeysY,
				BoneWeights = new float[mdl.BoneCount]
			};
			entries.Add(entry);
			try
			{
				entry.Events = included.Model.ReadSequenceEvents(sequenceStream, sequence.Index);
				entry.PoseParameters = included.Model.ReadPoseParameterNames(sequenceStream);
				if ((sequence.Flags & 0x80) != 0)
				{
					var range = included.Model.ReadPoseParameterRange(sequenceStream, sequence.CyclePoseIndex);
					entry.CycleParameter = entry.PoseParameters[sequence.CyclePoseIndex];
					entry.CycleMinimum = range.Minimum;
					entry.CycleMaximum = range.Maximum;
				}
				entry.Layers = included.Model.ReadSequenceAutoLayers(sequenceStream, sequence.Index).Select(layer => new SourceSequenceLayer
				{
					SequenceIndex = layer.SequenceIndex,
					PoseParameterIndex = layer.PoseParameterIndex,
					Flags = layer.Flags,
					Start = layer.Start,
					Peak = layer.Peak,
					Tail = layer.Tail,
					End = layer.End
				}).ToArray();
			}
			catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
			{
				entry.Error = exception.Message;
			}
			for (var bone = 0; bone < included.RootBoneIndices.Length; bone++)
			{
				if (included.RootBoneIndices[bone] >= 0)
					entry.BoneWeights[included.RootBoneIndices[bone]] = sequence.BoneWeights[bone];
			}

			var names = new List<string>();
			var pending = new HashSet<string>();
			if (sequence.GridWidth > 1 || sequence.GridHeight > 1)
				pending.Add("Pose parameter blend grid");
			if (sequence.AutoLayerCount != 0)
				pending.Add("Sequence auto layers");
			if (sequence.IkLockCount != 0)
				pending.Add("Sequence IK locks");
			if ((sequence.Flags & 4) != 0)
				pending.Add("Additive sequence requires a base pose");
			if ((sequence.Flags & 2) != 0)
				pending.Add("Snap transition");

			try
			{
				for (var blend = 0; blend < sequence.AnimationIndices.Length; blend++)
				{
					var key = (included.ModelPath, sequence.AnimationIndices[blend]);
					if (failures.TryGetValue(key, out var failure))
						throw new NotSupportedException(failure);
					using var stream = openFile(included.ModelPath);
					var descriptor = included.Model.GetAnimationDescriptor(stream, key.Item2);
					var first = mdl.ReadIncludedRawFrame(included, blend, 0, openFile);
					if (first.IkRuleCount != 0)
						pending.Add("Source IK targets and latch state");
					if (first.Movements.Length != 0)
						pending.Add("Root motion available separately");
					if (tracks.TryGetValue(key, out var existing))
					{
						names.Add(existing);
						continue;
					}

					try
					{
						var bytes = checked((long)descriptor.FrameCount * mdl.BoneCount * Marshal.SizeOf<Transform>());
						if (bytes > maximumPoseBytes - allocated)
							throw new NotSupportedException("Original animation channels exceed the 256 MiB per-model pose budget");
						var frames = new Transform[descriptor.FrameCount][];
						for (var frame = 0; frame < frames.Length; frame++)
						{
							var raw = frame == 0 ? first : mdl.ReadIncludedRawFrame(included, blend, frame, openFile);
							var cycle = frames.Length == 1 ? 0 : (float)frame / (frames.Length - 1);
							frames[frame] = raw.HierarchyCount == 0 ? raw.Pose
								: mdl.EvaluateIncludedHierarchy(included, descriptor.Index, frame, cycle, raw.Pose, openFile);
							if (frames[frame].Length != mdl.BoneCount || frames[frame].Any(transform => !transform.IsValid))
								throw new InvalidDataException("Invalid original animation pose");
						}

						var name = $"__source_track_{tracks.Count}";
						clips.Add(new(name, sequence.Activity, descriptor.Fps, descriptor.Looping, first.Additive, false, frames));
						tracks.Add(key, name);
						var metadata = new SourceSequenceTrack
						{
							Name = name,
							FrameCount = descriptor.FrameCount,
							FramesPerSecond = descriptor.Fps,
							Flags = descriptor.Flags,
							MovementDistance = first.Movements.Length == 0 ? 0 : first.Movements[^1].Position.Length
						};
						try
						{
							metadata.Ik = mdl.ExportTrackIk(included, blend, modelPath, openFile);
						}
						catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
						{
							metadata.IkError = exception.Message;
						}
						trackData.Add(metadata);
						names.Add(name);
						allocated += bytes;
					}
					catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
					{
						failures[key] = exception.Message;
						throw;
					}
				}
			}
			catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
			{
				entry.Error = exception.Message;
			}

			entry.Tracks = names.ToArray();
			entry.PendingOperations = pending.ToArray();
			if (names.Count == 1 && sequence.AnimationIndices.Length == 1 && animationNames.Add(sequence.Name))
			{
				var track = clips.First(clip => clip.Name == names[0]);
				entry.AnimationName = sequence.Name;
				clips.Add(track with { Name = sequence.Name, Activity = sequence.Activity, Looping = sequence.Looping, Delta = (sequence.Flags & 4) != 0 });
			}
		}

		return (clips.ToArray(), new SourceSequenceCatalog
		{
			Sequences = entries.ToArray(),
			Tracks = trackData.ToArray(),
			BoneNames = Enumerable.Range(0, mdl.BoneCount).Select(mdl.GetBoneName).ToArray(),
			BoneParents = mdl.Bones.Select(bone => bone.Parent).ToArray(),
			BindPose = mdl.Bones.Select(bone => new Transform(bone.Position, bone.Quaternion)).ToArray()
		});
	}
}
