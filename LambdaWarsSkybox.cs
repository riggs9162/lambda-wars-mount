using System;

namespace Sandbox.Mounting.Source1;

internal sealed class LambdaWarsSkybox
{
	private readonly record struct Node( int Plane, int Front, int Back );
	private readonly record struct Plane( Vector3 Normal, float Distance );
	private readonly record struct Leaf( int Contents, int Area );

	private readonly Node[] nodes;
	private readonly Plane[] planes;
	private readonly Leaf[] leaves;
	private readonly bool[] skyFaces;
	private readonly bool[] skyBrushes;
	private readonly int rootNode;

	internal Vector3 Origin { get; }
	internal float Scale { get; }
	internal int Area { get; }

	private LambdaWarsSkybox( Vector3 origin, float scale, int area, int rootNode, Node[] nodes, Plane[] planes, Leaf[] leaves, bool[] skyFaces, bool[] skyBrushes )
	{
		Origin = origin;
		Scale = scale;
		Area = area;
		this.rootNode = rootNode;
		this.nodes = nodes;
		this.planes = planes;
		this.leaves = leaves;
		this.skyFaces = skyFaces;
		this.skyBrushes = skyBrushes;
	}

	/// <summary>Separates the sky camera's BSP area from the playable world using Source leaf membership.</summary>
	internal static LambdaWarsSkybox TryCreate( LambdaWarsBsp bsp )
	{
		var camera = bsp.Entities.FirstOrDefault( entity => entity.GetValueOrDefault( "classname", "" ).Equals( "sky_camera", StringComparison.OrdinalIgnoreCase ) );
		if ( camera is null )
			return null;

		var origin = LambdaWarsBsp.Vector( camera.GetValueOrDefault( "origin", "0 0 0" ) );
		var scale = LambdaWarsEntityNumbers.Read( camera.GetValueOrDefault( "scale", "16" ) );
		if ( !float.IsFinite( origin.x ) || !float.IsFinite( origin.y ) || !float.IsFinite( origin.z ) || !float.IsFinite( scale ) || scale <= 0 )
			throw new InvalidDataException( "The sky camera origin or scale is invalid." );

		using var reader = bsp.Open();
		var nodeCount = RecordCount( bsp, 5, 32 );
		var planeCount = RecordCount( bsp, 1, 20 );
		var leafStride = bsp.Lumps[10].Version switch { 0 => 56, 1 => 32, _ => throw new InvalidDataException( "Unsupported BSP leaf version." ) };
		var leafCount = RecordCount( bsp, 10, leafStride );
		var faceCount = RecordCount( bsp, 7, 56 );
		var leafFaceCount = RecordCount( bsp, 16, 2 );
		var brushCount = RecordCount( bsp, 18, 12 );
		var leafBrushCount = RecordCount( bsp, 17, 2 );
		if ( bsp.Lumps[14].Length < 48 )
			throw new InvalidDataException( "The BSP world model is missing." );

		reader.BaseStream.Position = bsp.Lumps[14].Offset + 36;
		var rootNode = reader.ReadInt32();
		var nodes = new Node[nodeCount];
		for ( var i = 0; i < nodes.Length; i++ )
		{
			reader.BaseStream.Position = bsp.Lumps[5].Offset + i * 32;
			var plane = reader.ReadInt32();
			var front = reader.ReadInt32();
			var back = reader.ReadInt32();
			if ( (uint)plane >= planeCount )
				throw new InvalidDataException( "A BSP node references an invalid plane." );
			nodes[i] = new( plane, front, back );
		}

		var planes = new Plane[planeCount];
		for ( var i = 0; i < planes.Length; i++ )
		{
			reader.BaseStream.Position = bsp.Lumps[1].Offset + i * 20;
			var normal = LambdaWarsBsp.ReadVector( reader );
			var distance = reader.ReadSingle();
			if ( !float.IsFinite( normal.x ) || !float.IsFinite( normal.y ) || !float.IsFinite( normal.z ) || !float.IsFinite( distance ) )
				throw new InvalidDataException( "A BSP plane is non-finite." );
			planes[i] = new( normal, distance );
		}

		var leaves = new Leaf[leafCount];
		var faceRanges = new (int First, int Count)[leafCount];
		var brushRanges = new (int First, int Count)[leafCount];
		for ( var i = 0; i < leaves.Length; i++ )
		{
			reader.BaseStream.Position = bsp.Lumps[10].Offset + i * leafStride;
			var contents = reader.ReadInt32();
			reader.ReadInt16();
			var area = reader.ReadUInt16() & 0x1ff;
			reader.BaseStream.Position += 12;
			var first = reader.ReadUInt16();
			var count = reader.ReadUInt16();
			var firstBrush = reader.ReadUInt16();
			var countBrush = reader.ReadUInt16();
			if ( (long)first + count > leafFaceCount )
				throw new InvalidDataException( "A BSP leaf references invalid face indices." );
			if ( (long)firstBrush + countBrush > leafBrushCount )
				throw new InvalidDataException( "A BSP leaf references invalid brush indices." );
			leaves[i] = new( contents, area );
			faceRanges[i] = (first, count);
			brushRanges[i] = (firstBrush, countBrush);
		}

		var cameraLeaf = FindLeaf( origin, rootNode, nodes, planes, leaves.Length );
		if ( (leaves[cameraLeaf].Contents & 1) != 0 )
			throw new InvalidDataException( "The sky camera is inside a solid BSP leaf." );
		var skyArea = leaves[cameraLeaf].Area;
		var skyReferences = new bool[faceCount];
		var worldReferences = new bool[faceCount];
		var skyBrushReferences = new bool[brushCount];
		var worldBrushReferences = new bool[brushCount];
		for ( var i = 0; i < leaves.Length; i++ )
		{
			if ( (leaves[i].Contents & 1) != 0 )
				continue;
			var range = faceRanges[i];
			reader.BaseStream.Position = bsp.Lumps[16].Offset + range.First * 2;
			for ( var j = 0; j < range.Count; j++ )
			{
				var face = reader.ReadUInt16();
				if ( face >= faceCount )
					throw new InvalidDataException( "A BSP leaf references an invalid face." );
				if ( leaves[i].Area == skyArea )
					skyReferences[face] = true;
				else
					worldReferences[face] = true;
			}
			var brushRange = brushRanges[i];
			reader.BaseStream.Position = bsp.Lumps[17].Offset + brushRange.First * 2;
			for ( var j = 0; j < brushRange.Count; j++ )
			{
				var brush = reader.ReadUInt16();
				if ( brush >= brushCount )
					throw new InvalidDataException( "A BSP leaf references an invalid brush." );
				if ( leaves[i].Area == skyArea )
					skyBrushReferences[brush] = true;
				else
					worldBrushReferences[brush] = true;
			}
		}
		for ( var i = 0; i < skyReferences.Length; i++ )
			skyReferences[i] &= !worldReferences[i];
		for ( var i = 0; i < skyBrushReferences.Length; i++ )
			skyBrushReferences[i] &= !worldBrushReferences[i];

		return new( origin, scale, skyArea, rootNode, nodes, planes, leaves, skyReferences, skyBrushReferences );
	}

	internal bool IsSkyFace( int faceIndex ) => (uint)faceIndex < skyFaces.Length && skyFaces[faceIndex];
	internal bool IsSkyBrush( int brushIndex ) => (uint)brushIndex < skyBrushes.Length && skyBrushes[brushIndex];

	internal bool IsSkyPoint( Vector3 position ) => IsSkyLeaf( FindLeaf( position, rootNode, nodes, planes, leaves.Length ) );

	internal bool IsSkyLeaf( int leafIndex ) => (uint)leafIndex < leaves.Length && (leaves[leafIndex].Contents & 1) == 0 && leaves[leafIndex].Area == Area;

	/// <summary>Uses a static prop's authored leaves to avoid misclassifying large or off-center models.</summary>
	internal bool IsSkyProp( LambdaWarsStaticProp prop, LambdaWarsStaticPropMetadata metadata )
	{
		if ( prop.LeafCount == 0 )
			return IsSkyPoint( prop.Position );
		var inSky = false;
		for ( var i = prop.FirstLeaf; i < prop.FirstLeaf + prop.LeafCount; i++ )
		{
			var leafIndex = metadata.Leaves[i];
			if ( leafIndex >= leaves.Length )
				throw new InvalidDataException( "A static prop references an invalid BSP leaf." );
			if ( (leaves[leafIndex].Contents & 1) != 0 )
				continue;
			if ( leaves[leafIndex].Area != Area )
				return false;
			inSky = true;
		}
		return inSky;
	}

	private static int RecordCount( LambdaWarsBsp bsp, int lump, int stride )
	{
		if ( bsp.Lumps[lump].Length % stride != 0 || bsp.Lumps[lump].FourCc != 0 )
			throw new InvalidDataException( $"The BSP lump {lump} has an unsupported record layout." );
		return bsp.Lumps[lump].Length / stride;
	}

	private static int FindLeaf( Vector3 position, int rootNode, Node[] nodes, Plane[] planes, int leafCount )
	{
		var node = rootNode;
		for ( var steps = 0; node >= 0; steps++ )
		{
			if ( (uint)node >= nodes.Length || steps >= nodes.Length )
				throw new InvalidDataException( "The BSP node tree is invalid." );
			var branch = nodes[node];
			var plane = planes[branch.Plane];
			node = position.Dot( plane.Normal ) - plane.Distance >= 0 ? branch.Front : branch.Back;
		}
		var leaf = -(long)node - 1;
		if ( leaf >= leafCount )
			throw new InvalidDataException( "The BSP node tree references an invalid leaf." );
		return (int)leaf;
	}
}
