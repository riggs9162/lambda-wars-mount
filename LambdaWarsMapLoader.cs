using System;
using System.Text;

namespace Sandbox.Mounting.Source1;

internal sealed class LambdaWarsMapLoader( string filename ) : SceneLoader<LambdaWarsMount>
{
	private LambdaWarsImportBudget budget;

	protected override void BuildScene()
	{
		if ( !Host.MapsEnabled )
			throw new InvalidDataException( "Original 3D map conversion is disabled pending GPU validation." );
		using var currentBudget = new LambdaWarsImportBudget();
		budget = currentBudget;
		Host.ActiveMapImportBudget = currentBudget;
		try
		{
			BuildPreparedScene();
		}
		finally
		{
			Host.ActiveMapImportBudget = null;
			budget = null;
		}
	}

	private void BuildPreparedScene()
	{
		var prepared = Host.PrepareMapContent( filename, budget );
		var bsp = prepared.Bsp;
		var staticProps = prepared.StaticProps;
		budget.ReserveResources( prepared.DecodedUploadBytes );
		GC.Collect( GC.MaxGeneration, GCCollectionMode.Optimized, blocking: true, compacting: false );
		var model = LoadRegisteredGeometry( 0 );
		var world = new GameObject( true, filename );
		world.Components.Create<ModelRenderer>().Model = model;
		var collider = world.Components.Create<ModelCollider>();
		collider.Model = model;
		collider.Static = true;
        budget.SetStage( "Preparing brush entities" );
        AddBrushEntities( bsp );
        budget.SetStage( "Loading static props" );
        AddStaticProps( staticProps );
        budget.SetStage( "Loading entity props" );
        AddEntityProps( bsp );
        AddLighting( bsp );
        AddEntityMarkers( bsp );
		var navigation = world.Scene.NavMesh;
		navigation.IsEnabled = true;
		navigation.IncludeStaticBodies = true;
		navigation.IncludeKeyframedBodies = false;
		navigation.DeferGeneration = true;
		navigation.CustomBounds = true;
		navigation.Bounds = bsp.Bounds;
		navigation.AgentHeight = 64;
		navigation.AgentRadius = 16;
		navigation.AgentStepSize = 18;
		navigation.AgentMaxSlope = 40;
		navigation.ExcludedBodies.Add( "nav_exclude" );
	}

	private Model LoadRegisteredGeometry( int modelIndex )
	{
		var model = Model.Load( $"{Path}.model_{modelIndex}.vmdl" );
		if ( model is null || model.IsError )
			throw new InvalidDataException( $"Original BSP model {modelIndex} could not be loaded through its registered resource." );
		return model;
	}

	internal static Model BuildGeometry( LambdaWarsMount host, LambdaWarsBsp bsp, LambdaWarsImportBudget budget, string resourcePath, int modelIndex )
	{
		budget.SetStage( $"Reading geometry for BSP model {modelIndex}" );
		using var reader = bsp.Open();
		var vertices = new Vector3[bsp.Lumps[3].Length / 12];
		reader.BaseStream.Position = bsp.Lumps[3].Offset;
		for ( var i = 0; i < vertices.Length; i++ ) vertices[i] = LambdaWarsBsp.ReadVector( reader );

		var edges = new (int A, int B)[bsp.Lumps[12].Length / 4];
		reader.BaseStream.Position = bsp.Lumps[12].Offset;
		for ( var i = 0; i < edges.Length; i++ ) edges[i] = (reader.ReadUInt16(), reader.ReadUInt16());

		var surfEdges = new int[bsp.Lumps[13].Length / 4];
		reader.BaseStream.Position = bsp.Lumps[13].Offset;
		for ( var i = 0; i < surfEdges.Length; i++ ) surfEdges[i] = reader.ReadInt32();

		var textureInfos = new LambdaWarsBsp.TextureInfo[bsp.Lumps[6].Length / 72];
		for ( var i = 0; i < textureInfos.Length; i++ )
		{
			reader.BaseStream.Position = bsp.Lumps[6].Offset + i * 72;
			var u = new Vector4( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			var v = new Vector4( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
			reader.BaseStream.Position += 32;
			textureInfos[i] = new( u, v, reader.ReadInt32(), reader.ReadInt32() );
		}

		var textureData = new LambdaWarsBsp.TextureData[bsp.Lumps[2].Length / 32];
		for ( var i = 0; i < textureData.Length; i++ )
		{
			reader.BaseStream.Position = bsp.Lumps[2].Offset + i * 32 + 12;
			textureData[i] = new( reader.ReadInt32(), reader.ReadInt32(), reader.ReadInt32() );
		}

		var textureNames = new string[bsp.Lumps[44].Length / 4];
		var names = bsp.GetLump( 43 );
		for ( var i = 0; i < textureNames.Length; i++ )
		{
			reader.BaseStream.Position = bsp.Lumps[44].Offset + i * 4;
			var offset = reader.ReadInt32();
			var end = Array.IndexOf( names, (byte)0, offset );
			textureNames[i] = Encoding.UTF8.GetString( names, offset, end - offset );
		}

		reader.BaseStream.Position = bsp.Lumps[14].Offset + modelIndex * 48 + 40;
		var firstFace = reader.ReadInt32();
		var faceCount = reader.ReadInt32();
		var groups = new Dictionary<string, Geometry>( StringComparer.OrdinalIgnoreCase );
		var collisionVertices = new List<Vector3>();
		var collisionIndices = new List<int>();

		for ( var faceIndex = firstFace; faceIndex < firstFace + faceCount; faceIndex++ )
		{
			if ( (faceIndex & 255) == 0 )
				budget.Check();
			reader.BaseStream.Position = bsp.Lumps[7].Offset + faceIndex * 56;
			var plane = reader.ReadUInt16();
			reader.ReadByte();
			reader.ReadByte();
			var firstEdge = reader.ReadInt32();
			var edgeCount = reader.ReadInt16();
			var infoIndex = reader.ReadInt16();
			var displacement = reader.ReadInt16();
			if ( infoIndex < 0 || edgeCount < 3 ) continue;

			var info = textureInfos[infoIndex];
			var data = textureData[info.Data];
			var name = textureNames[data.Name];
			if ( name.StartsWith( "tools/", StringComparison.OrdinalIgnoreCase ) || (info.Flags & 0x84) != 0 ) continue;

			reader.BaseStream.Position = bsp.Lumps[1].Offset + plane * 20;
			var normal = LambdaWarsBsp.ReadVector( reader );
			var points = new Vector3[edgeCount];
			for ( var j = 0; j < points.Length; j++ )
			{
				var edgeIndex = surfEdges[firstEdge + j];
				var edge = edges[Math.Abs( edgeIndex )];
				points[j] = vertices[edgeIndex < 0 ? edge.B : edge.A];
			}

			if ( !groups.TryGetValue( name, out var group ) ) groups[name] = group = new();
			if ( displacement >= 0 ) AddDisplacement( bsp, reader, displacement, points, normal, info, data, group, collisionVertices, collisionIndices );
			else AddPolygon( points, normal, info, data, group );
		}

		budget.SetStage( $"Loading {groups.Count} materials for BSP model {modelIndex}" );
		var builder = Model.Builder.WithName( resourcePath );
		foreach ( var pair in groups )
		{
			budget.Check();
			var material = host.LoadContentMaterial( $"materials/{pair.Key}.vmt" );
			var mesh = new Mesh( material );
			mesh.CreateVertexBuffer( pair.Value.Vertices.Count, pair.Value.Vertices );
			mesh.CreateIndexBuffer( pair.Value.Indices.Count, pair.Value.Indices );
			builder.AddMesh( mesh );
		}
		budget.SetStage( $"Preparing collision for BSP model {modelIndex}" );
		if ( collisionIndices.Count > 0 )
			builder.AddCollisionMesh( collisionVertices, collisionIndices );
        AddBrushCollision( bsp, reader, builder, modelIndex, budget );
		budget.SetStage( $"Creating BSP model {modelIndex}" );
		return builder.Create();
	}

	private static void AddPolygon( Vector3[] points, Vector3 normal, LambdaWarsBsp.TextureInfo info, LambdaWarsBsp.TextureData data, Geometry group )
	{
		var index = group.Vertices.Count;
		foreach ( var point in points )
		{
			group.Vertices.Add( MakeVertex( point, normal, info, data ) );
		}
		for ( var i = 1; i < points.Length - 1; i++ )
		{
			group.Indices.AddRange( new[] { index, index + i + 1, index + i } );
		}
	}

	private static void AddDisplacement( LambdaWarsBsp bsp, BinaryReader reader, int displacement, Vector3[] corners, Vector3 normal, LambdaWarsBsp.TextureInfo info, LambdaWarsBsp.TextureData data, Geometry group, List<Vector3> collisionVertices, List<int> collisionIndices )
	{
		if ( corners.Length != 4 ) throw new InvalidDataException( "Displacement base is not a quad." );
		reader.BaseStream.Position = bsp.Lumps[26].Offset + displacement * 176;
		var start = LambdaWarsBsp.ReadVector( reader );
		var firstVertex = reader.ReadInt32();
		reader.ReadInt32();
		var power = reader.ReadInt32();
		if ( power is < 2 or > 4 ) throw new InvalidDataException( "Invalid displacement power." );
		var side = (1 << power) + 1;
		var nearest = Enumerable.Range( 0, 4 ).OrderBy( i => corners[i].DistanceSquared( start ) ).First();
		var c = Enumerable.Range( 0, 4 ).Select( i => corners[(nearest + i) % 4] ).ToArray();
		var index = group.Vertices.Count;
		var collision = collisionVertices.Count;

		for ( var y = 0; y < side; y++ )
		{
			for ( var x = 0; x < side; x++ )
			{
				var u = x / (float)(side - 1);
				var v = y / (float)(side - 1);
				var point = Vector3.Lerp( Vector3.Lerp( c[0], c[1], u ), Vector3.Lerp( c[3], c[2], u ), v );
				reader.BaseStream.Position = bsp.Lumps[33].Offset + (firstVertex + y * side + x) * 20;
				point += LambdaWarsBsp.ReadVector( reader ) * reader.ReadSingle();
				var authoredAlpha = reader.ReadSingle();
				if ( !float.IsFinite( authoredAlpha ) || authoredAlpha is < 0 or > 255 )
					throw new InvalidDataException( "Displacement blend alpha must be finite and between 0 and 255." );
				group.Vertices.Add( MakeVertex( point, normal, info, data, authoredAlpha / 255.0f ) );
				collisionVertices.Add( point );
			}
		}
		for ( var y = 0; y < side - 1; y++ )
		{
			for ( var x = 0; x < side - 1; x++ )
			{
				var a = y * side + x;
				var b = a + 1;
				var c1 = a + side;
				var d = c1 + 1;
				var triangles = (x + y) % 2 == 0 ? new[] { a, d, b, a, c1, d } : new[] { a, c1, b, b, c1, d };
				group.Indices.AddRange( triangles.Select( i => i + index ) );
				collisionIndices.AddRange( triangles.Select( i => i + collision ) );
			}
		}
	}

	private static MapVertex MakeVertex( Vector3 point, Vector3 normal, LambdaWarsBsp.TextureInfo info, LambdaWarsBsp.TextureData data, float terrainBlend = 0 )
	{
		var u = new Vector3( info.U.x, info.U.y, info.U.z );
		var v = new Vector3( info.V.x, info.V.y, info.V.z );
		return new MapVertex( point, normal, new Vector4( u.Normal, 1 ), new Vector2( (point.Dot( u ) + info.U.w) / Math.Max( data.Width, 1 ), (point.Dot( v ) + info.V.w) / Math.Max( data.Height, 1 ) ), new Vector4( 1, 1, 1, terrainBlend ) );
	}

	private void AddEntityProps( LambdaWarsBsp bsp )
	{
		foreach ( var entity in bsp.Entities )
		{
			var classname = entity.GetValueOrDefault( "classname", "" );
			if ( classname is not ("prop_physics" or "prop_physics_override" or "prop_dynamic" or "prop_dynamic_override") || !entity.TryGetValue( "model", out var modelName ) )
				continue;

			var model = Host.LoadContentModel( modelName );
			budget.Check();
			if ( model is null || model.IsError )
				throw new InvalidDataException( $"Map prop could not be loaded: {modelName}" );

			var prop = new GameObject( true, modelName );
			prop.WorldPosition = LambdaWarsBsp.Vector( entity.GetValueOrDefault( "origin", "0 0 0" ) );
			prop.WorldRotation = LambdaWarsBsp.Angle( entity.GetValueOrDefault( "angles", "0 0 0" ) );
			var renderer = prop.Components.Create<SkinnedModelRenderer>();
			renderer.UseAnimGraph = false;
			renderer.Model = model;
			var collider = prop.Components.Create<ModelCollider>();
			collider.Model = model;
			collider.Static = true;
		}
	}

	private static void AddLighting( LambdaWarsBsp bsp )
	{
		var environment = bsp.Entities.FirstOrDefault( entity => entity.GetValueOrDefault( "classname" ) == "light_environment" );
		if ( environment is null )
			return;

		var light = new GameObject( true, "light_environment" );
		var angles = LambdaWarsBsp.Angle( environment.GetValueOrDefault( "angles", "0 0 0" ) );
		var sourcePitch = environment.TryGetValue( "pitch", out var pitch )
			? LambdaWarsEntityNumbers.Read( pitch ) : 0;
		if ( !float.IsFinite( sourcePitch ) )
			throw new InvalidDataException( "The light_environment pitch must be finite." );
		light.WorldRotation = new Angles( -sourcePitch, angles.yaw, angles.roll );
		var directional = light.Components.Create<DirectionalLight>();
		directional.LightColor = ReadLightColor( environment.GetValueOrDefault( "_light", "255 255 255 200" ) );
	}

	private static Color ReadLightColor( string value )
	{
		var components = value.Split( ' ', StringSplitOptions.RemoveEmptyEntries ).Select( number => float.Parse( number, System.Globalization.CultureInfo.InvariantCulture ) ).ToArray();
		var brightness = components.Length > 3 ? components[3] / 200f : 1;
		return new Color( components[0] / 255f, components[1] / 255f, components[2] / 255f ) * brightness;
	}
	private void AddBrushEntities( LambdaWarsBsp bsp )
	{
		foreach ( var entity in bsp.Entities )
		{
			var classname = entity.GetValueOrDefault( "classname", "" );
			if ( classname is not ("func_brush" or "func_illusionary" or "func_wall" or "func_door" or "func_door_rotating") || !entity.TryGetValue( "model", out var modelName ) || !modelName.StartsWith( '*' ) )
				continue;

			var index = int.Parse( modelName[1..], System.Globalization.CultureInfo.InvariantCulture );
			var model = LoadRegisteredGeometry( index );
			var brush = new GameObject( true, entity.GetValueOrDefault( "targetname", classname ) );
			brush.WorldPosition = LambdaWarsBsp.Vector( entity.GetValueOrDefault( "origin", "0 0 0" ) );
			brush.WorldRotation = LambdaWarsBsp.Angle( entity.GetValueOrDefault( "angles", "0 0 0" ) );
			brush.Components.Create<ModelRenderer>().Model = model;
			if ( classname != "func_illusionary" )
			{
				var collider = brush.Components.Create<ModelCollider>();
				collider.Model = model;
				collider.Static = true;
			}
		}
	}

	private static HashSet<int> ModelBrushes( LambdaWarsBsp bsp, BinaryReader reader, int modelIndex )
	{
		reader.BaseStream.Position = bsp.Lumps[14].Offset + modelIndex * 48 + 36;
		var pending = new Stack<int>();
		pending.Push( reader.ReadInt32() );
		var brushes = new HashSet<int>();
		var visited = new HashSet<int>();
		while ( pending.Count > 0 )
		{
			var node = pending.Pop();
			if ( !visited.Add( node ) )
				continue;

			if ( node >= 0 )
			{
				reader.BaseStream.Position = bsp.Lumps[5].Offset + node * 32 + 4;
				pending.Push( reader.ReadInt32() );
				pending.Push( reader.ReadInt32() );
				continue;
			}

			var leaf = -node - 1;
			var stride = bsp.Lumps[10].Version == 0 ? 56 : 32;
			reader.BaseStream.Position = bsp.Lumps[10].Offset + leaf * stride + 24;
			var first = reader.ReadUInt16();
			var count = reader.ReadUInt16();
			for ( var i = 0; i < count; i++ )
			{
				reader.BaseStream.Position = bsp.Lumps[17].Offset + (first + i) * 2;
				brushes.Add( reader.ReadUInt16() );
			}
		}
		return brushes;
	}
	private void AddStaticProps( LambdaWarsStaticPropMetadata metadata )
	{
		var models = new Dictionary<string, Model>( StringComparer.OrdinalIgnoreCase );
		foreach ( var source in metadata.Props )
		{
			budget.Check();
			if ( !models.TryGetValue( source.ModelPath, out var model ) )
			{
				model = Host.LoadContentModel( source.ModelPath );
				if ( model is null || model.IsError )
					throw new InvalidDataException( $"Static prop could not be loaded: {source.ModelPath}" );

				models.Add( source.ModelPath, model );
			}

			budget.Check();
			var prop = new GameObject( true, source.ModelPath );
			prop.WorldPosition = source.Position;
			prop.WorldRotation = source.Rotation;
			ModelRenderer renderer;
			if ( model.BoneCount > 0 )
			{
				var skinned = prop.Components.Create<SkinnedModelRenderer>();
				skinned.UseAnimGraph = false;
				renderer = skinned;
			}
			else
			{
				renderer = prop.Components.Create<ModelRenderer>();
			}

			renderer.Model = model;
			if ( model.MaterialGroupCount > 0 )
			{
				if ( source.Skin >= model.MaterialGroupCount )
					throw new InvalidDataException( $"Static prop skin {source.Skin} is unavailable: {source.ModelPath}" );

				renderer.MaterialGroup = $"Skin {source.Skin}";
			}

			if ( source.Solid != 0 )
			{
				var collider = prop.Components.Create<ModelCollider>();
				collider.Model = model;
				collider.Static = true;
			}
		}
	}

	private static void AddEntityMarkers( LambdaWarsBsp bsp )
	{
		foreach ( var entity in bsp.Entities )
		{
			var classname = entity.GetValueOrDefault( "classname", "" );
			var tag = classname switch
			{
				"info_start_wars" => "rts_start",
				"capture_point" or "control_point" or "unit_control_point" => "rts_control_point",
				"scrap_marker" or "scrap_marker_small" => "rts_scrap",
				_ => null
			};
			if ( tag is null || !entity.TryGetValue( "origin", out var origin ) )
				continue;

			var marker = new GameObject( true, $"{classname}:{entity.GetValueOrDefault( "ownernumber", "" )}" );
			marker.WorldPosition = LambdaWarsBsp.Vector( origin );
			marker.Tags.Add( tag );
		}
	}

	private static void AddBrushCollision( LambdaWarsBsp bsp, BinaryReader reader, ModelBuilder builder, int modelIndex, LambdaWarsImportBudget budget )
	{
		var planes = new (Vector3 Normal, float Distance)[bsp.Lumps[1].Length / 20];
		for ( var i = 0; i < planes.Length; i++ )
		{
			reader.BaseStream.Position = bsp.Lumps[1].Offset + i * 20;
			planes[i] = (LambdaWarsBsp.ReadVector( reader ), reader.ReadSingle());
		}

		foreach ( var brush in ModelBrushes( bsp, reader, modelIndex ) )
		{
			budget.Check();
			reader.BaseStream.Position = bsp.Lumps[18].Offset + brush * 12;
			var firstSide = reader.ReadInt32();
			var sideCount = reader.ReadInt32();
			var contents = reader.ReadInt32();
			if ( (contents & 0x3000b) == 0 || sideCount < 4 )
				continue;
			if ( sideCount > 64 )
				throw new InvalidDataException( "A collision brush exceeds the current import work budget." );

			var sides = new (Vector3 Normal, float Distance)[sideCount];
			for ( var side = 0; side < sideCount; side++ )
			{
				reader.BaseStream.Position = bsp.Lumps[19].Offset + (firstSide + side) * 8;
				sides[side] = planes[reader.ReadUInt16()];
			}

			var hull = new List<Vector3>();
			for ( var a = 0; a < sideCount - 2; a++ )
			{
				for ( var b = a + 1; b < sideCount - 1; b++ )
				{
					for ( var c = b + 1; c < sideCount; c++ )
					{
						var cross = sides[b].Normal.Cross( sides[c].Normal );
						var determinant = sides[a].Normal.Dot( cross );
						if ( MathF.Abs( determinant ) < 0.00001f )
							continue;

						var point = (cross * sides[a].Distance + sides[c].Normal.Cross( sides[a].Normal ) * sides[b].Distance + sides[a].Normal.Cross( sides[b].Normal ) * sides[c].Distance) / determinant;
						var inside = true;
						for ( var side = 0; side < sides.Length; side++ )
						{
							if ( sides[side].Normal.Dot( point ) - sides[side].Distance <= 0.1f )
								continue;

							inside = false;
							break;
						}

						if ( !inside )
							continue;

						var duplicate = false;
						for ( var vertex = 0; vertex < hull.Count; vertex++ )
						{
							if ( hull[vertex].DistanceSquared( point ) >= 0.01f )
								continue;

							duplicate = true;
							break;
						}

						if ( !duplicate )
							hull.Add( point );
					}
				}
			}
			if ( hull.Count >= 4 )
				builder.AddCollisionHull( hull );
		}
	}
	[System.Runtime.InteropServices.StructLayout( System.Runtime.InteropServices.LayoutKind.Sequential )]
	private struct MapVertex( Vector3 position, Vector3 normal, Vector4 tangent, Vector2 texcoord, Vector4 sourceColor )
	{
		[VertexLayout.Position]
		public Vector3 Position = position;

		[VertexLayout.Normal]
		public Vector3 Normal = normal;

		[VertexLayout.Tangent]
		public Vector4 Tangent = tangent;

		[VertexLayout.TexCoord]
		public Vector2 Texcoord = texcoord;

		[VertexLayout.Color]
		public Vector4 SourceColor = sourceColor;
	}

	private sealed class Geometry
	{
		internal List<MapVertex> Vertices { get; } = new();
		internal List<int> Indices { get; } = new();
	}
}

internal sealed record LambdaWarsPreparedMap( LambdaWarsBsp Bsp, LambdaWarsStaticPropMetadata StaticProps, long DecodedUploadBytes );

/// <summary>Rebuilds a registered BSP submodel after scene unload while sharing only live scene-import budgets.</summary>
internal sealed class LambdaWarsBspModelLoader( string filename, int modelIndex ) : ResourceLoader<LambdaWarsMount>
{
	protected override object Load()
	{
		using var independentBudget = Host.ActiveMapImportBudget is null ? new LambdaWarsImportBudget() : null;
		var budget = Host.ActiveMapImportBudget ?? independentBudget;
		var prepared = Host.PrepareMapContent( filename, budget );
		if ( independentBudget is not null )
			budget.ReserveResources( prepared.DecodedUploadBytes );
		if ( modelIndex < 0 || modelIndex >= prepared.Bsp.Lumps[14].Length / 48 )
			throw new InvalidDataException( "The registered BSP model index is invalid." );
		return LambdaWarsMapLoader.BuildGeometry( Host, prepared.Bsp, budget, Path, modelIndex );
	}
}
