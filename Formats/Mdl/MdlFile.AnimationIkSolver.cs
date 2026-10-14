using System;

namespace Sandbox.Mounting.Source1;

internal sealed partial class MdlFile
{
	/// <summary>Solves an original three-link chain in model space with Source reach limits and the authored knee direction.</summary>
	internal Transform[] SolveAnimationIkChain(AnimationIkChain chain, Transform[] pose, Transform target)
	{
		if (pose.Length != BoneCount || chain.Bones.Length != 3 || !IsFinite(target.Position))
		{
			throw new ArgumentException("Invalid animation IK input.");
		}

		var thigh = chain.Bones[0];
		var knee = chain.Bones[1];
		var foot = chain.Bones[2];
		if ((uint)thigh >= BoneCount || (uint)knee >= BoneCount || (uint)foot >= BoneCount || Bones[knee].Parent != thigh || Bones[foot].Parent != knee)
		{
			throw new NotSupportedException("IK requires three directly connected original bones.");
		}

		var world = AnimationWorldPose(pose);
		var origin = world[thigh].Position;
		var upper = (world[knee].Position - origin).Length;
		var lower = (world[foot].Position - world[knee].Position).Length;
		var currentDistance = (world[foot].Position - origin).Length;
		if (upper <= 1e-6f || lower <= 1e-6f || currentDistance <= 1e-6f)
		{
			throw new InvalidDataException("Degenerate animation IK chain.");
		}

		var kneeDirection = chain.KneeDirection;
		if (kneeDirection.LengthSquared > 0)
		{
			kneeDirection = world[thigh].Rotation * kneeDirection;
		}
		else
		{
			if (currentDistance > (upper + lower) * 0.9998f)
			{
				throw new InvalidDataException("Straight IK chain has no authored knee direction.");
			}

			kneeDirection = (world[knee].Position - origin - (world[foot].Position - origin) * (upper / currentDistance)).Normal;
		}

		var relativeTarget = target.Position - origin;
		var minimumDistance = MathF.Max(MathF.Abs(upper - lower) * 1.15f, MathF.Min(upper, lower) * 0.15f);
		if (relativeTarget.Length > (upper + lower) * 0.9998f)
		{
			relativeTarget = relativeTarget.Normal * ((upper + lower) * 0.9998f);
		}
		else if (relativeTarget.Length < minimumDistance)
		{
			relativeTarget = (world[foot].Position - origin).Normal * minimumDistance;
		}

		var distance = relativeTarget.Length;
		var forward = relativeTarget / distance;
		var hintDistance = (upper + lower) * 100;
		var hint = world[knee].Position - origin + kneeDirection * hintDistance;
		var side = hint - forward * Vector3.Dot(hint, forward);
		if (side.LengthSquared < 1e-12f)
		{
			throw new InvalidDataException("IK knee hint is collinear with its target.");
		}

		var along = (distance + (upper * upper - lower * lower) / distance) * 0.5f;
		var perpendicularSquared = upper * upper - along * along;
		if (perpendicularSquared < -0.0001f)
		{
			throw new InvalidDataException("IK target cannot be reached within source chain limits.");
		}

		var relativeKnee = forward * along + side.Normal * MathF.Sqrt(MathF.Max(0, perpendicularSquared));
		var thighWorld = new Transform(origin, Rotation.LookAt(relativeKnee.Normal, world[thigh].Rotation.Up));
		var kneeWorld = new Transform(origin + relativeKnee, Rotation.LookAt((relativeTarget - relativeKnee).Normal, world[knee].Rotation.Up));
		var footWorld = new Transform(origin + relativeTarget, target.Rotation);
		var result = pose.ToArray();
		var parent = Bones[thigh].Parent;
		result[thigh] = parent < 0 ? thighWorld : world[parent].ToLocal(thighWorld);
		result[knee] = thighWorld.ToLocal(kneeWorld);
		result[foot] = kneeWorld.ToLocal(footWorld);

		return result;
	}
}
