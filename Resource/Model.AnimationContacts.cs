using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace Sandbox.Mounting.Source1;

internal sealed partial class SourceModelBuilder
{
	/// <summary>Original parent-local channels paired with lossless contact records; ground targets and persistent latching remain unevaluated.</summary>
	internal sealed record PreparedContactAnimation(PreparedAnimationClip RawClip, SourceAnimationManifest Manifest, long PoseBytes, long AggregatePoseBytes);

	/// <summary>Prepares the original 301-frame police pistol idle without registering it as a fully evaluated animation or dropping the last loop frame.</summary>
	internal static PreparedContactAnimation PreparePistolIdleContacts(MdlFile mdl, string modelPath, Func<string, Stream> openFile, long previouslyPreparedPoseBytes = 0)
	{
		const long maximumPoseBytes = 16 * 1024 * 1024;
		if (previouslyPreparedPoseBytes < 0 || previouslyPreparedPoseBytes > maximumPoseBytes)
			throw new ArgumentOutOfRangeException(nameof(previouslyPreparedPoseBytes));
		if (!modelPath.Equals("models/police.mdl", StringComparison.OrdinalIgnoreCase))
			throw new NotSupportedException("Contact-channel preparation currently supports original police pistolidle1 only.");

		var matches = mdl.ReadIncludedSequences(modelPath, openFile).Where(item => item.Sequence.Name == "pistolidle1").ToArray();
		if (matches.Length != 1)
			throw new InvalidDataException("Original pistolidle1 must resolve to exactly one included sequence.");
		var included = matches[0];
		var sequence = included.Sequence;
		if (sequence.Activity != "ACT_IDLE_PISTOL" || !sequence.Looping || (sequence.Flags & 6) != 0
			|| sequence.GridWidth != 1 || sequence.GridHeight != 1 || sequence.AnimationIndices.Length != 1
			|| sequence.AutoLayerCount != 0 || sequence.IkLockCount != 0)
			throw new NotSupportedException("Original pistol idle has unsupported sequence blending, transitions, layers or locks.");

		using var stream = openFile(included.ModelPath);
		var descriptor = included.Model.GetAnimationDescriptor(stream, sequence.AnimationIndices[0]);
		if (descriptor.FrameCount != 301 || descriptor.Fps != 30)
			throw new InvalidDataException("Original pistol idle must retain its 301 authored frames at 30 frames per second.");
		var poseBytes = checked((long)descriptor.FrameCount * mdl.BoneCount * Marshal.SizeOf<Transform>());
		var temporaryPoseBytes = checked(4L * mdl.BoneCount * Marshal.SizeOf<Transform>());
		if (poseBytes + temporaryPoseBytes > maximumPoseBytes - previouslyPreparedPoseBytes)
			throw new NotSupportedException("Original contact channels exceed the aggregate 16 MiB pose-memory budget.");

		var manifest = mdl.ExportPistolIdleManifest(modelPath, openFile);
		if (manifest.SequenceName != sequence.Name || manifest.Activity != sequence.Activity
			|| manifest.FrameCount != descriptor.FrameCount || manifest.FramesPerSecond != descriptor.Fps
			|| manifest.SequenceFlags != sequence.Flags || manifest.AnimationFlags != descriptor.Flags
			|| manifest.AnimationIndex != descriptor.Index || manifest.BoneNames.Length != mdl.BoneCount
			|| manifest.BoneParents.Length != mdl.BoneCount || manifest.Rules.Length == 0
			|| manifest.PendingOperations.Length != 2 || !manifest.PendingOperations.Contains("Ground IK target resolution")
			|| !manifest.PendingOperations.Contains("Persistent Source target accumulation and foot latching"))
			throw new InvalidDataException("Original contact manifest does not match the sampled sequence and pending ground operations.");
		foreach (var rule in manifest.Rules)
		{
			if (rule.Record.Length != 152 || BinaryPrimitives.ReadInt32LittleEndian(rule.Record.AsSpan(4, 4)) is not (3 or 4))
				throw new NotSupportedException("Original pistol idle contact preparation requires only losslessly recorded ground and release IK rules.");
		}

		var frames = new Transform[descriptor.FrameCount][];
		var sawGroundRequirement = false;
		for (var frame = 0; frame < frames.Length; frame++)
		{
			var cycle = (float)frame / (frames.Length - 1);
			var sample = mdl.SampleIncludedSequenceBones(included, cycle, 0, 0, openFile);
			if (sample.PendingOperations.Any(operation => operation != "Ground IK targets and latch state")
				|| sample.Additive || !sample.Looping || sample.Fps != descriptor.Fps)
				throw new NotSupportedException("Original pistol idle has additional unevaluated sequence operations beyond ground contacts.");
			sawGroundRequirement |= sample.PendingOperations.Contains("Ground IK targets and latch state");
			var raw = mdl.ReadIncludedRawFrame(included, 0, frame, openFile);
			if (raw.Additive || raw.HierarchyCount != 0 || raw.Movements.Length != 0 || raw.IkRuleCount != manifest.Rules.Length
				|| raw.Pose.Length != mdl.BoneCount || raw.Pose.Any(transform => !transform.IsValid))
				throw new NotSupportedException("Original pistol idle raw channels contain unsupported postprocessing or invalid parent-local poses.");
			frames[frame] = raw.Pose;
		}

		if (!sawGroundRequirement)
			throw new InvalidDataException("Original pistol idle contact metadata has no sampled ground-contact requirement.");
		var clip = new PreparedAnimationClip(sequence.Name, sequence.Activity, descriptor.Fps, true, false, false, frames);
		return new(clip, manifest, poseBytes, checked(previouslyPreparedPoseBytes + poseBytes));
	}
}
