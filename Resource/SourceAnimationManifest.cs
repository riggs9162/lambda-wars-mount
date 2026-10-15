using Sandbox.ModelEditor;

namespace Sandbox.Mounting.Source1;

/// <summary>Version 1 shared node schema, duplicated across mount/game assemblies; rule records retain original little-endian Source bytes.</summary>
[GameData("lambda_wars_source_animation")]
public sealed class SourceAnimationManifest
{
	public int SchemaVersion { get; set; } = 1;
	public string ModelPath { get; set; } = "";
	public string SourceModelPath { get; set; } = "";
	public string SequenceName { get; set; } = "";
	public string Activity { get; set; } = "";
	public int SequenceFlags { get; set; }
	public int AnimationFlags { get; set; }
	public int AnimationIndex { get; set; }
	public int FrameCount { get; set; }
	public float FramesPerSecond { get; set; }
	public string[] BoneNames { get; set; } = [];
	public int[] BoneParents { get; set; } = [];
	public int[] SourceToRootBones { get; set; } = [];
	public string[] PendingOperations { get; set; } = [];
	public SourceAnimationChain[] Chains { get; set; } = [];
	public SourceAnimationRule[] Rules { get; set; } = [];
}

public sealed class SourceAnimationChain
{
	public string Name { get; set; } = "";
	public int[] RootBones { get; set; } = [];
	public byte[] LinkRecords { get; set; } = [];
}

public sealed class SourceAnimationRule
{
	public byte[] Record { get; set; } = [];
	public string Attachment { get; set; } = "";
	public int ErrorSampleCount { get; set; }
	public byte[] CompressedErrors { get; set; } = [];
	public byte[] UncompressedErrors { get; set; } = [];
}