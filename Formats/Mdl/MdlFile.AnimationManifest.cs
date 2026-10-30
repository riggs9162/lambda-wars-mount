using System;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed partial class MdlFile
{
	/// <summary>Exports the original pistol idle's lossless IK records without enabling its unevaluated channels as a complete animation.</summary>
	internal SourceAnimationManifest ExportPistolIdleManifest(string modelPath, Func<string, Stream> openFile)
	{
		if (!modelPath.Equals("models/police.mdl", StringComparison.OrdinalIgnoreCase))
		{
			throw new NotSupportedException("The first runtime IK manifest slice is original police pistolidle1 only.");
		}

		var matches = ReadIncludedSequences(modelPath, openFile).Where(item => item.Sequence.Name == "pistolidle1").ToArray();
		if (matches.Length != 1)
		{
			throw new InvalidDataException("Original pistolidle1 must resolve to one included sequence.");
		}

		var included = matches[0];
		var sequence = included.Sequence;
		if (sequence.AnimationIndices.Length != 1 || sequence.AutoLayerCount != 0 || sequence.IkLockCount != 0)
		{
			throw new NotSupportedException("Pistol idle runtime manifest requires one track without layers or sequence locks.");
		}

		using var stream = openFile(included.ModelPath);
		var descriptor = included.Model.GetAnimationDescriptor(stream, sequence.AnimationIndices[0]);
		var result = new SourceAnimationManifest
		{
			ModelPath = modelPath,
			SourceModelPath = included.ModelPath,
			SequenceName = sequence.Name,
			Activity = sequence.Activity,
			SequenceFlags = sequence.Flags,
			AnimationFlags = descriptor.Flags,
			AnimationIndex = descriptor.Index,
			FrameCount = descriptor.FrameCount,
			FramesPerSecond = descriptor.Fps,
			BoneNames = Enumerable.Range(0, BoneCount).Select(GetBoneName).ToArray(),
			BoneParents = Bones.Select(bone => bone.Parent).ToArray(),
			SourceToRootBones = included.RootBoneIndices.ToArray(),
			PendingOperations = ["Ground IK target resolution", "Persistent Source target accumulation and foot latching"]
		};
		included.Model.ExportLosslessIk(stream, descriptor.Index, included.RootBoneIndices, openFile, result);

		return result;
	}

	internal SourceAnimationManifest ExportTrackIk(IncludedSequence included, int blend, string modelPath, Func<string, Stream> openFile)
	{
		using var stream = openFile(included.ModelPath);
		var index = included.Sequence.AnimationIndices[blend];
		var descriptor = included.Model.GetAnimationDescriptor(stream, index);
		var offset = included.Model.AnimationDescriptorOffset(index);
		stream.Position = offset;
		var animation = LumpReader<StudioAnimDesc>.ReadSingleFromStream(stream);
		if (animation.NumIkRules == 0 || animation.IkRuleIndex == 0 && animation.AnimBlockIkRuleIndex == 0)
			return null;

		var result = new SourceAnimationManifest
		{
			ModelPath = modelPath,
			SourceModelPath = included.ModelPath,
			SequenceName = included.Sequence.Name,
			SequenceFlags = included.Sequence.Flags,
			AnimationIndex = index,
			AnimationFlags = descriptor.Flags,
			FrameCount = descriptor.FrameCount,
			FramesPerSecond = descriptor.Fps,
			SourceToRootBones = included.RootBoneIndices
		};
		included.Model.ExportLosslessIk(stream, index, included.RootBoneIndices, openFile, result);
		return result;
	}

	private void ExportLosslessIk(Stream stream, int animationIndex, int[] mapping, Func<string, Stream> openFile, SourceAnimationManifest output)
	{
		var budget = new AnimationManifestByteBudget();
		var offset = AnimationDescriptorOffset(animationIndex);
		stream.Position = offset;
		var animation = LumpReader<StudioAnimDesc>.ReadSingleFromStream(stream);
		if (animation.NumIkRules is < 1 or > 256 || animation.NumFrames is < 1 or > 4096)
		{
			throw new InvalidDataException("IK manifest exceeds its bounded original rule/frame counts.");
		}

		using (var data = new ReferencePoseData(stream, 0, _header.Length))
		{
			var chains = ReadAnimationIkChains(stream);
			output.Chains = chains.Select((chain, index) =>
			{
				var record = _header.IkChainOffset + index * 16L;
				var bones = chain.Bones.Select(bone => mapping[bone]).ToArray();
				if (bones.Any(bone => bone < 0))
				{
					throw new NotSupportedException("An original IK chain contains an unmapped root bone.");
				}

				return new SourceAnimationChain { Name = chain.Name, RootBones = bones, LinkRecords = ManifestBytes(data, record + data.Int(record + 12), 84, budget) };
			}).ToArray();
		}

		void Read(ReferencePoseData data, long basis, int relative)
		{
			if (relative <= 0)
			{
				throw new InvalidDataException("Missing original IK manifest records.");
			}

			output.Rules = new SourceAnimationRule[animation.NumIkRules];
			for (var index = 0; index < output.Rules.Length; index++)
			{
				var record = checked(basis + relative + index * 152L);
				var start = data.Float(record + 76);
				var end = data.Float(record + 88);
				if (!float.IsFinite(start) || !float.IsFinite(end) || end < start || end - start > 2)
				{
					throw new InvalidDataException("Invalid original IK error interval.");
				}

				var count = checked((int)MathF.Ceiling((end - start) * (animation.NumFrames - 1)) + 1);
				var rule = new SourceAnimationRule { Record = ManifestBytes(data, record, 152, budget), ErrorSampleCount = count };
				var compressed = data.Int(record + 60);
				var uncompressed = data.Int(record + 72);
				if (compressed != 0)
				{
					var block = checked(record + compressed);
					long last = block + 36;
					for (var axis = 0; axis < 6; axis++)
					{
						var channel = data.Short(block + 24 + axis * 2L);
						if (channel == 0)
						{
							continue;
						}

						if (channel < 36)
						{
							throw new InvalidDataException("Invalid original IK compressed channel offset.");
						}

						var run = checked(block + channel);
						var covered = 0;
						while (covered < count)
						{
							var valid = data.Byte(run);
							var total = data.Byte(run + 1);
							if (valid > total)
								throw new InvalidDataException("Invalid original IK compressed error run.");
							if (total == 0)
							{
								run += 2;
								break;
							}

							covered += total;
							run = checked(run + (valid + 1) * 2L);
						}

						last = Math.Max(last, run);
					}

					rule.CompressedErrors = ManifestBytes(data, block, checked((int)(last - block)), budget);
				}
				else if (uncompressed != 0)
				{
					rule.UncompressedErrors = ManifestBytes(data, checked(record + uncompressed), checked(count * 28), budget);
				}

				var attachment = data.Int(record + 120);
				if (attachment != 0 && data.Int(record + 4) == 5)
				{
					var name = new List<byte>();
					for (var character = 0; character < 4096; character++)
					{
						var value = data.Byte(record + attachment + character);
						if (value == 0)
						{
							rule.Attachment = System.Text.Encoding.UTF8.GetString(name.ToArray());
							break;
						}
						if (value < 32)
							throw new InvalidDataException("Invalid IK attachment name.");
						name.Add(value);
					}
					if (rule.Attachment.Length == 0 && name.Count != 0)
						throw new InvalidDataException("Unterminated IK attachment name.");
				}

				output.Rules[index] = rule;
			}
		}

		if (animation.IkRuleIndex != 0)
		{
			using var data = new ReferencePoseData(stream, 0, _header.Length);
			Read(data, offset, animation.IkRuleIndex);
		}
		else
		{
			WithAnimationAuxiliaryData(stream, animation, offset, openFile, (data, basis) => Read(data, basis, animation.AnimBlockIkRuleIndex));
		}
	}

	private sealed class AnimationManifestByteBudget
	{
		public int Remaining { get; set; } = 25 * 1024 * 1024;
	}

	private static byte[] ManifestBytes(ReferencePoseData data, long offset, int count, AnimationManifestByteBudget budget)
	{
		if (count < 0 || count > 1024 * 1024)
		{
			throw new InvalidDataException("Original animation manifest record exceeds its byte budget.");
		}

		if (count > budget.Remaining)
		{
			throw new InvalidDataException("Original animation manifest exceeds its aggregate 25 MiB byte budget.");
		}

		budget.Remaining -= count;
		var bytes = new byte[count];
		for (var index = 0; index < count; index++)
		{
			bytes[index] = data.Byte(checked(offset + index));
		}

		return bytes;
	}
}
