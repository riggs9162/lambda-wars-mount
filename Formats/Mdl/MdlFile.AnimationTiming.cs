using System;
using System.Collections.Generic;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed partial class MdlFile
{
	internal readonly record struct AnimationTimingContribution(float Weight, float FramesPerSecond, int FrameCount);

	internal readonly record struct SequenceTiming(float CyclesPerSecond, float DurationSeconds, bool Looping)
	{
		public bool IsStatic => CyclesPerSecond == 0;

		public float CycleAtTime(float seconds)
		{
			if (!float.IsFinite(seconds))
			{
				throw new ArgumentOutOfRangeException(nameof(seconds));
			}

			if (IsStatic)
			{
				return 0;
			}

			var cycle = seconds * CyclesPerSecond;

			if (!float.IsFinite(cycle))
			{
				throw new ArgumentOutOfRangeException(nameof(seconds));
			}

			return Looping ? cycle - MathF.Floor(cycle) : Math.Clamp(cycle, 0, 1);
		}
	}

	/// <summary>Returns Source Studio_CPS timing using bilinear timing weights and authored pose parameter values, independently of triangular bone-pose blending.</summary>
	internal SequenceTiming ReadSequenceTiming(Stream stream, SequenceDescriptor sequence, float parameterX, float parameterY)
	{
		if (!float.IsFinite(parameterX) || !float.IsFinite(parameterY))
		{
			throw new ArgumentOutOfRangeException(nameof(parameterX));
		}

		if (sequence.GridWidth < 1 || sequence.GridHeight < 1 || sequence.KeysX.Length != sequence.GridWidth || sequence.KeysY.Length != sequence.GridHeight || sequence.AnimationIndices.Length != sequence.GridWidth * sequence.GridHeight)
		{
			throw new InvalidDataException("Invalid sequence timing blend grid.");
		}

		var (x, sx) = sequence.ParameterX < 0 ? (0, 0f) : AnimationGridCoordinate(sequence.KeysX, parameterX);
		var (y, sy) = sequence.ParameterY < 0 ? (0, 0f) : AnimationGridCoordinate(sequence.KeysY, parameterY);
		var contributions = new AnimationTimingContribution[4];
		var cursor = 0;

		for (var row = 0; row < 2; row++)
		{
			for (var column = 0; column < 2; column++)
			{
				var weight = (column == 0 ? 1 - sx : sx) * (row == 0 ? 1 - sy : sy);

				if (weight > 0)
				{
					var index = Math.Min(x + column, sequence.GridWidth - 1) + Math.Min(y + row, sequence.GridHeight - 1) * sequence.GridWidth;
					var offset = AnimationDescriptorOffset(sequence.AnimationIndices[index]);
					ValidateRange<StudioAnimDesc>(stream, offset, 1);
					stream.Position = offset;
					var animation = LumpReader<StudioAnimDesc>.ReadSingleFromStream(stream);
					contributions[cursor] = new(weight, animation.Fps, animation.NumFrames);
				}

				cursor++;
			}
		}

		return CalculateSequenceTiming(contributions, sequence.Looping);
	}

	internal SequenceTiming ReadIncludedSequenceTiming(IncludedSequence included, float parameterX, float parameterY, Func<string, Stream> openFile)
	{
		using var stream = openFile(included.ModelPath);

		return included.Model.ReadSequenceTiming(stream, included.Sequence, parameterX, parameterY);
	}

	/// <summary>Matches Source Studio_CPS and Studio_Duration; single-frame and zero-rate contributions add no cycle rate and a static sequence has zero duration.</summary>
	internal static SequenceTiming CalculateSequenceTiming(IReadOnlyList<AnimationTimingContribution> contributions, bool looping)
	{
		if (contributions is null || contributions.Count > 4)
		{
			throw new ArgumentException("Source sequence timing requires at most four contributions.", nameof(contributions));
		}

		var cyclesPerSecond = 0f;

		foreach (var contribution in contributions)
		{
			if (!float.IsFinite(contribution.Weight) || contribution.Weight < 0 || contribution.Weight > 1)
			{
				throw new InvalidDataException("Invalid sequence timing blend weight.");
			}

			if (contribution.Weight == 0)
			{
				continue;
			}

			if (!float.IsFinite(contribution.FramesPerSecond) || contribution.FramesPerSecond < 0 || contribution.FrameCount < 1 || contribution.FrameCount > 65536)
			{
				throw new InvalidDataException("Invalid source sequence timing contribution.");
			}

			if (contribution.FrameCount > 1)
			{
				cyclesPerSecond += contribution.FramesPerSecond / (contribution.FrameCount - 1) * contribution.Weight;
			}
		}

		var duration = cyclesPerSecond == 0 ? 0 : 1 / cyclesPerSecond;

		if (!float.IsFinite(cyclesPerSecond) || !float.IsFinite(duration))
		{
			throw new InvalidDataException("Source sequence timing overflows its finite range.");
		}

		return new(cyclesPerSecond, duration, looping);
	}
}
