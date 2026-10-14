using System;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed partial class MdlFile
{
	internal sealed record AnimationIkEvaluation(Transform[] Pose, AnimationIkRuleSample[] Rules, string[] PendingOperations);

	/// <summary>Evaluates original self targets and three-link chains; ground, attachment and latch state require a world-aware target provider.</summary>
	internal AnimationIkEvaluation EvaluateIncludedIkRules(IncludedSequence included, int animationIndex, float cycle, Transform[] pose, Func<string, Stream> openFile, Func<AnimationIkRuleSample, AnimationIkChain, Transform[], Transform?> resolveTarget)
	{
		using var stream = openFile(included.ModelPath);
		var rules = included.Model.SampleAnimationIkRules(stream, animationIndex, cycle, openFile);
		var sourceChains = included.Model.ReadAnimationIkChains(stream);
		var pending = new HashSet<string>();
		var world = AnimationWorldPose(pose);
		var targets = new Dictionary<int, Transform>();
		var chains = new Dictionary<int, AnimationIkChain>();

		foreach (var rule in rules)
		{
			var sourceChain = sourceChains[rule.Chain];
			var mapping = sourceChain.Bones.Select(index => included.RootBoneIndices[index]).ToArray();
			if (mapping.Any(index => index < 0))
			{
				pending.Add($"IK chain '{sourceChain.Name}' contains unmapped bones");
				continue;
			}

			var chain = sourceChain with
			{
				Bones = mapping
			};
			chains[rule.Chain] = chain;
			var originalTarget = world[mapping[2]];
			var accumulated = targets.GetValueOrDefault(rule.Chain, originalTarget);
			if (rule.Type == 6)
			{
				if (resolveTarget is null)
				{
					pending.Add("IK unlatch requires persistent target state");
				}
				else
				{
					resolveTarget(rule, chain, world);
				}

				continue;
			}

			if (rule.Weight <= 0)
			{
				continue;
			}

			Transform target;
			if (rule.Type == 1)
			{
				var bone = rule.Bone < 0 ? -1 : included.RootBoneIndices[rule.Bone];
				if (rule.Bone >= 0 && bone < 0)
				{
					pending.Add("IK self target references an unmapped bone");
					continue;
				}

				target = bone < 0 ? rule.Error : world[bone].ToWorld(rule.Error);
			}
			else if (rule.Type == 4)
			{
				target = originalTarget;
			}
			else if (rule.Type == 2)
			{
				throw new NotSupportedException("Original Source marks IK_WORLD as unsupported.");
			}
			else
			{
				var resolved = resolveTarget?.Invoke(rule, chain, world);
				if (!resolved.HasValue)
				{
					pending.Add(rule.Type == 3 ? "Ground IK requires a world target and foot latch state" : "Attachment IK requires a world attachment target");
					continue;
				}

				target = resolved.Value;
			}

			targets[rule.Chain] = new Transform(accumulated.Position + (target.Position - accumulated.Position) * rule.Weight, Rotation.Slerp(accumulated.Rotation, target.Rotation, rule.Weight));
		}

		var result = pose.ToArray();

		foreach (var target in targets)
		{
			result = SolveAnimationIkChain(chains[target.Key], result, target.Value);
		}

		return new(result, rules, pending.ToArray());
	}
}
