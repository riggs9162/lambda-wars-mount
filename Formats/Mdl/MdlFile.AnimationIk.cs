using System;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed partial class MdlFile
{
	internal sealed record AnimationIkChain(string Name, int[] Bones, Vector3 KneeDirection);
	internal sealed record AnimationIkRuleSample(int Index, int Type, int Chain, int Bone, int Slot, float Height, float Radius, float Floor, float Weight, bool ShouldLatch, float Contact, float Drop, float Top, Transform Error, string Attachment);
	internal AnimationIkChain[] ReadAnimationIkChains(Stream stream)
	{
		if (_header.NumIkChains < 0 || _header.NumIkChains > 256)
		{
			throw new InvalidDataException("Invalid animation IK chain count.");
		}

		using var data = new ReferencePoseData(stream, 0, _header.Length);
		var result = new AnimationIkChain[_header.NumIkChains];

		for (var chain = 0; chain < result.Length; chain++)
		{
			var record = checked(_header.IkChainOffset + chain * 16L);
			var name = ReadOptionalRelativeString(stream, record, data.Int(record));
			var count = data.Int(record + 8);
			var links = data.Int(record + 12);
			if (count != 3 || links <= 0)
			{
				throw new NotSupportedException("Only original three-link IK chains are supported.");
			}

			var bones = new int[count];

			for (var link = 0; link < count; link++)
			{
				bones[link] = data.Int(record + links + link * 28L);
				if ((uint)bones[link] >= BoneCount)
				{
					throw new InvalidDataException("IK chain references an invalid bone.");
				}
			}

			result[chain] = new(name, bones, data.Vector96(record + links + 4));
		}

		return result;
	}

	internal AnimationIkRuleSample[] SampleAnimationIkRules(Stream stream, int animationIndex, float cycle, Func<string, Stream> openFile)
	{
		return SampleAnimationIkRulesCore(stream, animationIndex, cycle, openFile, true);
	}

	internal AnimationIkRuleSample[] ReadAnimationIkRuleMetadata(Stream stream, int animationIndex, float cycle, Func<string, Stream> openFile)
	{
		return SampleAnimationIkRulesCore(stream, animationIndex, cycle, openFile, false);
	}

	private AnimationIkRuleSample[] SampleAnimationIkRulesCore(Stream stream, int animationIndex, float cycle, Func<string, Stream> openFile, bool decodeErrors)
	{
		if (!float.IsFinite(cycle))
		{
			throw new ArgumentOutOfRangeException(nameof(cycle));
		}

		var offset = AnimationDescriptorOffset(animationIndex);
		ValidateRange<StudioAnimDesc>(stream, offset, 1);
		stream.Position = offset;
		var animation = LumpReader<StudioAnimDesc>.ReadSingleFromStream(stream);
		if (animation.NumIkRules < 0 || animation.NumIkRules > 256)
		{
			throw new InvalidDataException("Invalid animation IK rule count.");
		}

		var result = new AnimationIkRuleSample[animation.NumIkRules];
		if (animation.IkRuleIndex == 0 && animation.AnimBlockIkRuleIndex == 0)
		{
			return[];
		}

		void Read(ReferencePoseData data, long baseOffset, int ruleOffset)
		{
			if (result.Length != 0 && ruleOffset <= 0)
			{
				throw new InvalidDataException("Missing animation IK records.");
			}

			for (var rule = 0; rule < result.Length; rule++)
			{
				var record = checked(baseOffset + ruleOffset + rule * 152L);
				var type = data.Int(record + 4);
				var chain = data.Int(record + 8);
				var bone = data.Int(record + 12);
				var slot = data.Int(record + 16);
				var compressed = data.Int(record + 60);
				var firstFrame = data.Int(record + 68);
				var errorIndex = data.Int(record + 72);
				var start = data.Float(record + 76);
				var peak = data.Float(record + 80);
				var tail = data.Float(record + 84);
				var end = data.Float(record + 88);
				if (type < 1 || type > 6 || chain < 0 || chain >= _header.NumIkChains || bone < -1 || bone >= BoneCount || slot < 0 || slot > 255 || firstFrame < 0 || start > peak || peak > tail || tail > end)
				{
					throw new InvalidDataException("Invalid source animation IK rule.");
				}

				var adjustedCycle = end > 1 && cycle < start ? cycle + 1 : cycle;
				var relativeCycle = Math.Clamp(adjustedCycle, start, end) - start;
				var frame = (animation.NumFrames - 1) * relativeCycle;
				var weight = adjustedCycle < start || adjustedCycle >= end ? 0 : adjustedCycle < peak ? (adjustedCycle - start) / (peak - start) : adjustedCycle <= tail ? 1 : (end - adjustedCycle) / (end - tail);
				weight = weight * weight * (3 - 2 * weight);
				var error = new Transform(Vector3.Zero, Rotation.Identity);
				if (!decodeErrors || (type != 3 && weight < 0.0001f))
				{
					error = new Transform(Vector3.Zero, Rotation.Identity);
				}
				else if (compressed != 0)
				{
					error = SampleCompressedAnimationTransform(data, record + compressed, frame);
				}
				else if (errorIndex != 0)
				{
					Transform Sample(int integer)
					{
						var sampleOffset = record + errorIndex + integer * 28L;
						var position = data.Vector96(sampleOffset);
						var rotation = new Rotation(data.Float(sampleOffset + 12), data.Float(sampleOffset + 16), data.Float(sampleOffset + 20), data.Float(sampleOffset + 24));

						return new Transform(position, rotation.Normal);
					}

					var first = Sample((int)frame);
					var fraction = frame - (int)frame;
					var second = fraction > 0.001f ? Sample((int)frame + 1) : first;
					error = new Transform(first.Position + (second.Position - first.Position) * fraction, Rotation.Lerp(first.Rotation, second.Rotation, fraction));
				}
				else if (type != 4 && type != 6)
				{
					throw new InvalidDataException("Source IK target has no authored error data.");
				}

				var attachmentOffset = data.Int(record + 120);
				var attachment = "";
				if (attachmentOffset != 0)
				{
					var bytes = new List<byte>();

					for (var character = 0; character < 4096; character++)
					{
						var value = data.Byte(record + attachmentOffset + character);
						if (value == 0)
						{
							attachment = System.Text.Encoding.UTF8.GetString(bytes.ToArray());
							break;
						}

						bytes.Add(value);
					}

					if (attachment.Length == 0 && bytes.Count != 0)
					{
						throw new InvalidDataException("Unterminated IK attachment name.");
					}
				}

				result[rule] = new(data.Int(record), type, chain, bone, slot, data.Float(record + 20), data.Float(record + 24), data.Float(record + 28), weight, adjustedCycle >= peak && adjustedCycle < end, data.Float(record + 96), data.Float(record + 100), data.Float(record + 104), error, attachment);
			}
		}

		if (animation.IkRuleIndex != 0)
		{
			using var data = new ReferencePoseData(stream, 0, _header.Length);
			Read(data, offset, animation.IkRuleIndex);
		}
		else
		{
			WithAnimationAuxiliaryData(stream, animation, offset, openFile, (data, baseOffset) => Read(data, baseOffset, animation.AnimBlockIkRuleIndex));
		}

		return result;
	}
}
