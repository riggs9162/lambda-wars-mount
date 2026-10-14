using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Sandbox.Mounting.Source1;

internal sealed partial class MdlFile
{
	internal sealed record RawAnimationFrame(AnimationDescriptor Descriptor, Transform[] Pose, int IkRuleCount, int HierarchyCount, AnimationMovement[] Movements, bool Additive)
	{
		public bool RequiresPostprocessing => Additive || IkRuleCount != 0 || HierarchyCount != 0 || Movements.Length != 0;
	}

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	internal struct AnimationMovement
	{
		public int EndFrame;
		public int MotionFlags;
		public float StartVelocity;
		public float EndVelocity;
		public float Yaw;
		public Vector3 Direction;
		public Vector3 Position;
	}

	internal AnimationMovement[] ReadAnimationMovements(Stream stream, int index)
	{
		var offset = AnimationDescriptorOffset(index);
		ValidateRange<StudioAnimDesc>(stream, offset, 1);
		stream.Position = offset;
		var animation = LumpReader<StudioAnimDesc>.ReadSingleFromStream(stream);
		if (animation.NumMovements < 0 || animation.NumMovements > 65536)
		{
			throw new InvalidDataException("Invalid movement count.");
		}

		var result = new AnimationMovement[animation.NumMovements];
		ValidateRange<AnimationMovement>(stream, offset + animation.MovementIndex, result.Length);
		stream.Position = offset + animation.MovementIndex;
		var previous = 0;

		for (var i = 0; i < result.Length; i++)
		{
			result[i] = LumpReader<AnimationMovement>.ReadSingleFromStream(stream);
			var movement = result[i];
			if (movement.EndFrame <= previous || movement.EndFrame >= animation.NumFrames || !float.IsFinite(movement.StartVelocity) || !float.IsFinite(movement.EndVelocity) || !float.IsFinite(movement.Yaw) || !IsFinite(movement.Direction) || !IsFinite(movement.Position))
			{
				throw new InvalidDataException("Invalid source animation movement block.");
			}

			previous = movement.EndFrame;
		}

		return result;
	}

	/// <summary>Samples authored cumulative animation motion without applying it to gameplay objects.</summary>
	internal static Transform SampleAnimationMovement(AnimationMovement[] movements, float frame)
	{
		if (!float.IsFinite(frame) || frame < 0)
		{
			throw new ArgumentOutOfRangeException(nameof(frame));
		}

		var position = Vector3.Zero;
		var yaw = 0f;
		var previousFrame = 0;

		foreach (var movement in movements)
		{
			if (frame <= movement.EndFrame)
			{
				var fraction = (frame - previousFrame) / (movement.EndFrame - previousFrame);
				var distance = movement.StartVelocity * fraction + 0.5f * (movement.EndVelocity - movement.StartVelocity) * fraction * fraction;

				return new Transform(position + movement.Direction * distance, Rotation.FromYaw(yaw + (movement.Yaw - yaw) * fraction));
			}

			position = movement.Position;
			yaw = movement.Yaw;
			previousFrame = movement.EndFrame;
		}

		return new Transform(position, Rotation.FromYaw(yaw));
	}
}
