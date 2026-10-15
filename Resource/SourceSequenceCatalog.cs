using Sandbox.ModelEditor;

namespace Sandbox.Mounting.Source1;

/// <summary>Original sequence catalog; tracks contain parent-local channels and require the listed Source operations during unit playback.</summary>
[GameData("lambda_wars_source_sequences")]
public sealed class SourceSequenceCatalog
{
	public int SchemaVersion { get; set; } = 2;
	public string[] BoneNames { get; set; } = [];
	public int[] BoneParents { get; set; } = [];
	public Transform[] BindPose { get; set; } = [];
	public SourceSequenceTrack[] Tracks { get; set; } = [];
	public SourceSequenceEntry[] Sequences { get; set; } = [];
}

public sealed class SourceSequenceEntry
{
	public string Name { get; set; } = "";
	public string AnimationName { get; set; } = "";
	public string SourceModelPath { get; set; } = "";
	public int SourceIndex { get; set; }
	public string Activity { get; set; } = "";
	public int Flags { get; set; }
	public int GridWidth { get; set; }
	public int GridHeight { get; set; }
	public string CycleParameter { get; set; } = "";
	public float CycleMinimum { get; set; }
	public float CycleMaximum { get; set; }
	public int ParameterX { get; set; }
	public int ParameterY { get; set; }
	public float[] KeysX { get; set; } = [];
	public float[] KeysY { get; set; } = [];
	public float[] BoneWeights { get; set; } = [];
	public string[] Tracks { get; set; } = [];
	public string[] PoseParameters { get; set; } = [];
	public SourceSequenceLayer[] Layers { get; set; } = [];
	public string[] PendingOperations { get; set; } = [];
	public string Error { get; set; } = "";
}


public sealed class SourceSequenceLayer
{
	public int SequenceIndex { get; set; }
	public int PoseParameterIndex { get; set; }
	public int Flags { get; set; }
	public float Start { get; set; }
	public float Peak { get; set; }
	public float Tail { get; set; }
	public float End { get; set; }
}

public sealed class SourceSequenceTrack
{
	public string Name { get; set; } = "";
	public int FrameCount { get; set; }
	public float FramesPerSecond { get; set; }
	public int Flags { get; set; }
	public float MovementDistance { get; set; }
	public SourceAnimationManifest Ik { get; set; }
	public string IkError { get; set; } = "";
}
