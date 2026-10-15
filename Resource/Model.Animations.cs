using System;
using System.Collections.Generic;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed partial class SourceModelBuilder
{
	internal sealed record PreparedAnimationClip(string Name, string Activity, float FramesPerSecond, bool Looping, bool Delta, bool DisableInterpolation, Transform[][] Frames);

	internal sealed record AnimationImportPlan(PreparedAnimationClip[] Clips, string[] Rejections);

	private static readonly Dictionary<string, string[]> RosterAnimationActivities = new(StringComparer.OrdinalIgnoreCase)
	{
		["models/police.mdl"] = ["ACT_IDLE_PISTOL", "ACT_WALK_PISTOL", "ACT_RUN_PISTOL", "ACT_RANGE_ATTACK_PISTOL", "ACT_IDLE_SMG1", "ACT_WALK_RIFLE", "ACT_RUN_RIFLE", "ACT_RANGE_ATTACK_SMG1"],
		["models/humans/group03/male_07.mdl"] = ["ACT_IDLE_SMG1_RELAXED", "ACT_WALK_RIFLE", "ACT_RUN_RIFLE", "ACT_RANGE_ATTACK_SMG1"],
		["models/humans/group03/male_05_worker.mdl"] = ["ACT_IDLE", "ACT_WALK_RIFLE", "ACT_RUN_RIFLE", "ACT_RANGE_ATTACK_SMG1", "ACT_BUILDING"],
		["models/stalker.mdl"] = ["ACT_IDLE", "ACT_WALK", "ACT_RANGE_ATTACK1"]
	};

	private void AddOriginalAnimations(ModelBuilder builder, MdlFile mdl)
	{
		if (Host is not LambdaWarsMount || mdl.BoneCount == 0 || mdl.IsStaticProp)
		{
			return;
		}

		var catalog = PrepareSequenceCatalog(mdl, FullPath, Host.OpenContentFile);

		var plan = PrepareOriginalAnimations(mdl, FullPath, Host.OpenContentFile);

		builder.WithData(catalog.Catalog);

		foreach (var sequence in catalog.Catalog.Sequences.Where(sequence => sequence.Error.Length != 0))
		{
			Log.Trace($"{FullPath}: original sequence {sequence.SourceModelPath}:{sequence.Name} channels were not imported: {sequence.Error}");
		}

		foreach (var clip in plan.Clips.Concat(catalog.Clips.Where(clip => !plan.Clips.Any(prepared => prepared.Name == clip.Name))))
		{
			var animation = builder.AddAnimation(clip.Name, clip.FramesPerSecond)
				.WithLooping(clip.Looping)
				.WithDelta(clip.Delta)
				.WithInterpolationDisabled(clip.DisableInterpolation);

			foreach (var frame in clip.Frames)
			{
				animation.AddFrame(frame.AsSpan());
			}
		}

		if (plan.Rejections.Length != 0)
		{
			Log.Trace($"{FullPath}: original sequence import rejected: {string.Join("; ", plan.Rejections)}");
		}
	}

	/// <summary>Prepares only roster activities with fully supported sequence operations and original parent-local frames; unsupported clips remain explicit diagnostics.</summary>
	internal static AnimationImportPlan PrepareOriginalAnimations(MdlFile mdl, string modelPath, Func<string, Stream> openFile)
	{
		if (!RosterAnimationActivities.TryGetValue(modelPath.Replace('\\', '/'), out var activities))
		{
			return new([], []);
		}

		const int maximumClips = 32;
		const int maximumFrames = 4096;
		const long maximumPoseBytes = 16 * 1024 * 1024;
		var wanted = new HashSet<string>(activities, StringComparer.Ordinal);
		var sequences = mdl.ReadIncludedSequences(modelPath, openFile)
			.Where(included => wanted.Contains(included.Sequence.Activity))
			.ToArray();
		var duplicateNames = sequences.GroupBy(included => included.Sequence.Name, StringComparer.Ordinal)
			.Where(group => group.Count() > 1)
			.Select(group => group.Key)
			.ToHashSet(StringComparer.Ordinal);
		var clips = new List<PreparedAnimationClip>();
		var rejected = new List<string>();
		var allocatedBytes = 0L;

		foreach (var activity in activities)
		{
			if (!sequences.Any(sequence => sequence.Sequence.Activity == activity))
			{
				rejected.Add($"{activity}: absent from original model and includes");
			}
		}

		foreach (var included in sequences)
		{
			var sequence = included.Sequence;

			try
			{
				if (duplicateNames.Contains(sequence.Name))
				{
					throw new NotSupportedException("Duplicate original sequence name needs virtual-model resolution");
				}

				if (clips.Count >= maximumClips)
				{
					throw new NotSupportedException("Roster animation count exceeds its import budget");
				}

				using var stream = openFile(included.ModelPath);
				var descriptors = sequence.AnimationIndices.Select(index => included.Model.GetAnimationDescriptor(stream, index)).ToArray();
				var frameCount = descriptors.Max(descriptor => descriptor.FrameCount);
				var poseBytes = checked((long)frameCount * mdl.BoneCount * System.Runtime.InteropServices.Marshal.SizeOf<Transform>());

				if (frameCount > maximumFrames || poseBytes > maximumPoseBytes - allocatedBytes)
				{
					throw new NotSupportedException("Original sequence exceeds its native frame-memory budget");
				}

				if (sequence.GridWidth != 1 || sequence.GridHeight != 1)
				{
					throw new NotSupportedException("Pose-parameter sequence needs runtime parameter evaluation rather than a frozen cooked blend");
				}

				if ((sequence.Flags & 2) != 0)
				{
					throw new NotSupportedException("Original snap sequence requires transition control, not disabled frame interpolation");
				}

				var timing = mdl.ReadIncludedSequenceTiming(included, 0, 0, openFile);
				var rate = descriptors[0].Fps;

				if (!float.IsFinite(rate) || rate <= 0)
				{
					throw new NotSupportedException("A zero-rate original sequence cannot be passed to the native animation builder");
				}

				if (frameCount > 1 && rate / (frameCount - 1) != timing.CyclesPerSecond)
				{
					throw new InvalidDataException("Original single-track frame rate does not match Source sequence cycle timing");
				}

				var frames = new Transform[frameCount][];

				for (var frame = 0; frame < frameCount; frame++)
				{
					var cycle = frameCount == 1 ? 0 : (float)frame / (frameCount - 1);
					var sample = mdl.SampleIncludedSequenceBones(included, cycle, 0, 0, openFile);
					var pending = sample.PendingOperations.Where(operation => operation != "Additive sequence requires a base pose").ToArray();

					if (pending.Length != 0)
					{
						throw new NotSupportedException(string.Join(", ", pending));
					}

					if (sample.Pose.Length != mdl.BoneCount || sample.Pose.Any(transform => !transform.IsValid))
					{
						throw new InvalidDataException("Original animation frame has an invalid bone pose");
					}

					if (sequence.AutoLayerCount == 0)
					{
						var raw = mdl.ReadIncludedRawFrame(included, 0, frame, openFile);
						frames[frame] = raw.HierarchyCount == 0
							? raw.Pose
							: mdl.EvaluateIncludedHierarchy(included, sequence.AnimationIndices[0], frame, cycle, raw.Pose, openFile);
					}
					else
					{
						frames[frame] = sample.Pose;
					}
				}

				allocatedBytes += poseBytes;
				clips.Add(new(sequence.Name, sequence.Activity, rate, sequence.Looping, (sequence.Flags & 4) != 0, false, frames));
			}
			catch (Exception exception) when (exception is IOException or NotSupportedException)
			{
				rejected.Add($"{included.ModelPath}:{sequence.Name}: {exception.Message}");
			}
		}

		return new(clips.ToArray(), rejected.ToArray());
	}
}
