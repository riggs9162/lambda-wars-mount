using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Sandbox.Mounting.Source1;

/// <summary>Where one animation track lives: the model that holds it, its animation index and its frame count.</summary>
internal readonly record struct SourceTrackSource( MdlFile.IncludedSequence Origin, int AnimationIndex, int FrameCount );

/// <summary>Decodes original animation tracks when they are first played, so memory follows the tracks in use rather than every authored sequence.</summary>
internal sealed class SourceTrackDecoder( MdlFile model, SourceTrackSource[] sources )
{
	private const long MaximumTrackBytes = 64L * 1024 * 1024;

	/// <summary>Returns one flat array holding every frame's bones in the root model's bone order.</summary>
	internal Transform[] Decode( int track, Func<string, Stream> openFile )
	{
		if ( (uint)track >= sources.Length )
			throw new ArgumentOutOfRangeException( nameof( track ) );

		var source = sources[track];
		var bones = model.BoneCount;
		if ( (long)source.FrameCount * bones * Marshal.SizeOf<Transform>() > MaximumTrackBytes )
			throw new NotSupportedException( "Original animation track exceeds its 64 MiB pose budget." );

		using var files = new PagedFiles( openFile );
		var frames = new Transform[source.FrameCount * bones];
		for ( var frame = 0; frame < source.FrameCount; frame++ )
		{
			var raw = model.ReadIncludedAnimationFrame( source.Origin, source.AnimationIndex, frame, files.Open );
			var cycle = source.FrameCount == 1 ? 0 : (float)frame / (source.FrameCount - 1);
			var pose = raw.HierarchyCount == 0 ? raw.Pose
				: model.EvaluateIncludedHierarchy( source.Origin, source.AnimationIndex, frame, cycle, raw.Pose, files.Open );
			if ( pose.Length != bones || pose.Any( transform => !transform.IsValid ) )
				throw new InvalidDataException( "Invalid original animation pose." );
			pose.CopyTo( frames, frame * bones );
		}
		return frames;
	}
}
