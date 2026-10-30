using System;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed partial class SourceModelBuilder
{
	/// <summary>Describes every original sequence and unique blend track without decoding frames, baking pose parameters or fixing world-dependent IK into a pose.</summary>
	internal static (SourceSequenceCatalog Catalog, SourceTrackSource[] Sources) PrepareSequenceCatalog(MdlFile mdl, string modelPath, Func<string, Stream> openFile)
	{
		var sources = new List<SourceTrackSource>();
		var entries = new List<SourceSequenceEntry>();
		var trackData = new List<SourceSequenceTrack>();
		var tracks = new Dictionary<(string Path, int Index), string>();
		var failures = new Dictionary<(string Path, int Index), string>();
		var origins = new Dictionary<string, MdlFile.IncludedSequence>(StringComparer.OrdinalIgnoreCase);

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
				Error = sequence.AnimationIndices.Length == 0 ? "Empty sequence has no animation tracks." : "",
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
					var summary = included.Model.ReadAnimationSummary(stream, key.Item2);
					var descriptor = summary.Descriptor;
					if (summary.IkRuleCount != 0)
						pending.Add("Source IK targets and latch state");
					if (summary.Movements.Length != 0)
						pending.Add("Root motion available separately");
					if (tracks.TryGetValue(key, out var existing))
					{
						names.Add(existing);
						continue;
					}

					try
					{
						var name = $"__source_track_{tracks.Count}";
						tracks.Add(key, name);
						if (!origins.TryGetValue(included.ModelPath, out var origin))
							origins.Add(included.ModelPath, origin = included with { Sequence = null });
						sources.Add(new(origin, key.Item2, descriptor.FrameCount));
						var metadata = new SourceSequenceTrack
						{
							Name = name,
							FrameCount = descriptor.FrameCount,
							FramesPerSecond = descriptor.Fps,
							Flags = descriptor.Flags,
							MovementDistance = summary.Movements.Length == 0 ? 0 : summary.Movements[^1].Position.Length
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
		}

		return (new SourceSequenceCatalog
		{
			ModelPath = modelPath,
			Sequences = entries.ToArray(),
			Tracks = trackData.ToArray(),
			BoneNames = Enumerable.Range(0, mdl.BoneCount).Select(mdl.GetBoneName).ToArray(),
			BoneParents = mdl.Bones.Select(bone => bone.Parent).ToArray(),
			BindPose = mdl.Bones.Select(bone => new Transform(bone.Position, bone.Quaternion)).ToArray()
		}, sources.ToArray());
	}
}
