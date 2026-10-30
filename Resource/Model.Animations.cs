using System;

namespace Sandbox.Mounting.Source1;

internal sealed partial class SourceModelBuilder
{
	internal sealed record PreparedAnimationClip(string Name, string Activity, float FramesPerSecond, bool Looping, bool Delta, bool DisableInterpolation, Transform[][] Frames);

	/// <summary>Marks a skeletal model as having original sequences. The catalog and frames stay out of the model, so they cost memory only for the models and tracks the game plays.</summary>
	private void AddOriginalAnimations(ModelBuilder builder, MdlFile mdl)
	{
		if (Host is not LambdaWarsMount || mdl.BoneCount == 0 || mdl.IsStaticProp)
		{
			return;
		}

		builder.WithData(new SourceSequenceCatalog { ModelPath = FullPath });
	}
}
