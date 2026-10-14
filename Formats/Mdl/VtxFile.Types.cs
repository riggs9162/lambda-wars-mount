using System.Runtime.InteropServices;

namespace Sandbox.Mounting.Source1;

internal sealed partial class VtxFile
{
	[StructLayout( LayoutKind.Sequential, Pack = 1 )]
	private struct MaterialReplacementHeader
	{
		public short MaterialId;
		public int ReplacementMaterialNameOffset;
	}

	[StructLayout( LayoutKind.Sequential, Pack = 1 )]
	private struct MaterialReplacementListHeader
	{
		public int NumReplacements;
		public int ReplacementOffset;
	}

	[StructLayout( LayoutKind.Sequential, Pack = 1 )]
	private struct BodyPartHeader
	{
		public int NumModels;
		public int ModelOffset;
	}

	[StructLayout( LayoutKind.Sequential, Pack = 1 )]
	private struct ModelHeader
	{
		public int NumLods;
		public int LodOffset;
	}

	[StructLayout( LayoutKind.Sequential, Pack = 1 )]
	private struct ModelLodHeader
	{
		public int NumMeshes;
		public int MeshOffset;
		public float SwitchPoint;
	}

	[StructLayout( LayoutKind.Sequential, Pack = 1 )]
	private struct MeshHeader
	{
		public int NumStripGroups;
		public int StripGroupHeaderOffset;
		public byte MeshFlags;
	}

	[StructLayout( LayoutKind.Sequential, Pack = 1 )]
	private struct StripGroupHeader
	{
		public int NumVerts;
		public int VertOffset;

		public int NumIndices;
		public int IndexOffset;

		public int NumStrips;
		public int StripOffset;

		public byte Flags;
	}

	private enum StripHeaderFlags : byte
	{
		None = 0,
		IsTriList = 1,
		IsTriStrip = 2
	}

	[StructLayout( LayoutKind.Explicit, Size = 33 )]
	private struct SwarmStripGroupHeader
	{
		[FieldOffset( 0 )] public StripGroupHeader Header;
		[FieldOffset( 25 )] public int NumTopologyIndices;
		[FieldOffset( 29 )] public int TopologyOffset;
	}

	[StructLayout( LayoutKind.Explicit, Size = 35 )]
	private struct SwarmStripHeader
	{
		[FieldOffset( 0 )] public StripHeader Header;
		[FieldOffset( 27 )] public int NumTopologyIndices;
		[FieldOffset( 31 )] public int TopologyOffset;
	}

	[StructLayout( LayoutKind.Sequential, Pack = 1 )]
	private struct StripHeader
	{
		public int NumIndices;
		public int IndexOffset;

		public int NumVerts;
		public int VertOffset;

		public short NumBones;
		public StripHeaderFlags Flags;

		public int NumBoneStateChanges;
		public int BoneStateChangeOffset;
	}

	[StructLayout( LayoutKind.Sequential, Pack = 1 )]
	private struct OptimizedVertex
	{
		public byte BoneWeightIndex0;
		public byte BoneWeightIndex1;
		public byte BoneWeightIndex2;
		public byte NumBones;

		public ushort OrigMeshVertId;

		public sbyte BoneId0;
		public sbyte BoneId1;
		public sbyte BoneId2;
	}

	private struct MeshData
	{
		public int IndexOffset;
		public int IndexCount;
		public int VertexOffset;
		public int VertexCount;
	}
}
