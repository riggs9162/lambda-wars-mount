using System;
using System.Text;
using System.Threading.Tasks;

namespace Sandbox.Mounting.Source1;

internal sealed class LambdaWarsMapLoader( string filename, bool skyOnly = false ) : SceneLoader<LambdaWarsMount>
{
	private static readonly Vector3 WavePointClearance = Vector3.One * 1024;

	private const int SolidContents = 0x1;
	private const int GrateContents = 0xA;
	private const int NpcClipContents = 0x20000;
	private const float BlockerSliceSize = 20;

	/// <summary>Sorts brushes into the collision they belong to by their Source contents.</summary>
	internal enum BrushLayer
	{
		Solid,
		Grate,
		Entity,
		NpcClip
	}

	private LambdaWarsImportBudget budget;

	protected override void BuildScene()
	{
		if ( !Host.MapsEnabled )
			throw new InvalidDataException( "Original 3D map conversion is disabled pending GPU validation." );
		if ( skyOnly && Host.ActiveMapImportBudget is not null )
		{
			budget = Host.ActiveMapImportBudget;
			BuildPreparedScene();
			return;
		}
		var importScene = Game.ActiveScene;
		try
		{
			GC.Collect( GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false );
			GC.WaitForPendingFinalizers();
			GC.Collect( GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: false );
			using var currentBudget = new LambdaWarsImportBudget();
			budget = currentBudget;
			Host.ActiveMapImportBudget = currentBudget;
			BuildPreparedScene();
		}
		catch
		{
			_ = DestroyFailedScene( importScene );
			throw;
		}
		finally
		{
			Host.ActiveMapImportBudget = null;
			budget = null;
		}
	}

	/// <summary>Releases a failed import after the scene loader has dispatched its queued component callbacks.</summary>
	private static async Task DestroyFailedScene( Scene scene )
	{
		await Task.Yield();
		await GameTask.MainThread();
		if ( scene.IsValid() )
		{
			using ( scene.Push() )
				scene.Destroy();
		}
	}

	private void BuildPreparedScene()
	{
		var prepared = Host.PrepareMapContent( filename, budget );
		var bsp = prepared.Bsp;
		var staticProps = prepared.StaticProps;
		var sky = LambdaWarsSkybox.TryCreate( bsp );
		budget.ReserveResources( prepared.DecodedUploadBytes );
		if ( skyOnly )
		{
			if ( sky is null )
				return;
			var skyWorld = new GameObject( true, "3D sky geometry" );
			var skyModel = Model.Load( $"mount://{Host.Ident}/{filename}.scene.sky.vmdl" );
			if ( skyModel is null || skyModel.IsError )
				throw new InvalidDataException( "The 3D sky geometry could not be loaded." );
			skyWorld.Components.Create<ModelRenderer>().Model = skyModel;
			AddBrushEntities( bsp, sky, true );
			AddStaticProps( staticProps, sky, true );
			AddEntityProps( bsp, sky, true );
			AddLighting( bsp );
			return;
		}
		var model = LoadRegisteredGeometry( 0 );
		var world = new GameObject( true, filename );
		world.Components.Create<ModelRenderer>().Model = model;
		var collider = world.Components.Create<ModelCollider>();
		collider.Model = model;
		collider.Static = true;
        budget.SetStage( "Preparing brush entities" );
        AddBrushEntities( bsp, sky, false );
        budget.SetStage( "Loading static props" );
        AddStaticProps( staticProps, sky, false );
        budget.SetStage( "Loading entity props" );
        AddEntityProps( bsp, sky, false );
        AddLighting( bsp );
        AddEntityMarkers( bsp );
		if ( bsp.Entities.Any( entity => entity.GetValueOrDefault( "classname", "" ) == "overrun_wave_spawnpoint" ) )
			AddOffMeshLinks( bsp );
		if ( sky is not null )
		{
			var scene = SceneFile.Load( $"mount://{Host.Ident}/{filename}.sky.scene" );
			if ( scene is null )
				throw new InvalidDataException( "The 3D sky scene could not be imported." );
			var skyObject = new GameObject( true, "3D sky" );
			skyObject.WorldPosition = -sky.Origin * sky.Scale;
			skyObject.WorldScale = Vector3.One * sky.Scale;
			skyObject.Components.Create<Skybox3D>().SkyboxScene = scene;
		}
		budget.SetStage( "Preparing NPC clip volumes" );
		AddNpcClip( bsp, sky );
		budget.SetStage( "Preparing grates and windows" );
		AddGrates( bsp, sky );
		var navigation = world.Scene.NavMesh;
		navigation.IsEnabled = true;
		navigation.IncludeStaticBodies = true;
		navigation.IncludeKeyframedBodies = false;
		navigation.DeferGeneration = true;
		navigation.CustomBounds = true;
		navigation.Bounds = NavigationBounds( bsp );
		navigation.AgentHeight = 64;
		navigation.AgentRadius = 16;
		navigation.AgentStepSize = 18;
		navigation.AgentMaxSlope = 40;
		navigation.ExcludedBodies.Add( "nav_exclude" );
	}

	/// <summary>Reads the authored player regions that bound camera movement, building and navigation.</summary>
	internal static BBox[] PlayableRegions( LambdaWarsBsp bsp ) => bsp.Entities
		.Where( entity => entity.GetValueOrDefault( "classname", "" ) == "func_map_boundary" && entity.GetValueOrDefault( "StartDisabled", "0" ) != "1" )
		.Select( entity =>
		{
			var bounds = bsp.EntityBounds( entity );
			var bloat = LambdaWarsEntityNumbers.Read( entity.GetValueOrDefault( "bloat", "-32" ) );
			return new BBox( bounds.Mins - Vector3.One * bloat, bounds.Maxs + Vector3.One * bloat );
		} )
		.Where( bounds => bounds.Size.x > 0 && bounds.Size.y > 0 && bounds.Size.z > 0 ).ToArray();

	/// <summary>Limits navigation generation to the playable regions horizontally while keeping the map's full height range and reaching any Overrun wave points placed beyond them.</summary>
	private static BBox NavigationBounds( LambdaWarsBsp bsp )
	{
		var regions = PlayableRegions( bsp );
		if ( regions.Length == 0 )
			return bsp.Bounds;

		var world = bsp.Bounds;
		var areas = regions.Concat( bsp.Entities.Where( entity => entity.GetValueOrDefault( "classname", "" ) == "overrun_wave_spawnpoint" )
			.Select( entity => BBox.FromPositionAndSize( LambdaWarsBsp.Vector( entity.GetValueOrDefault( "origin", "0 0 0" ) ), WavePointClearance ) ) ).ToArray();
		return new BBox(
			new Vector3( Math.Max( world.Mins.x, areas.Min( r => r.Mins.x ) ), Math.Max( world.Mins.y, areas.Min( r => r.Mins.y ) ), world.Mins.z ),
			new Vector3( Math.Min( world.Maxs.x, areas.Max( r => r.Maxs.x ) ), Math.Min( world.Maxs.y, areas.Max( r => r.Maxs.y ) ), world.Maxs.z ) );
	}

	/// <summary>Imports NPC clip brushes as invisible static collision tagged "npcclip" with matching navigation blockers so pathing and unit movement respect them while ground, sight and weapon traces ignore them.</summary>
	private void AddNpcClip( LambdaWarsBsp bsp, LambdaWarsSkybox sky )
	{
		var hullBounds = new List<BBox>();
		var clip = AddBrushLayer( bsp, sky, BrushLayer.NpcClip, "npcclip", "npcclip", hullBounds );
		if ( clip is null )
			return;

		var blockers = new GameObject( true, "npcclip_nav" ) { Parent = clip };
		foreach ( var bounds in hullBounds )
		{
			var area = blockers.Components.Create<NavMeshArea>();
			area.IsBlocker = true;
			area.SceneVolume = new Sandbox.Volumes.SceneVolume { Type = Sandbox.Volumes.SceneVolume.VolumeTypes.Box, Box = bounds };
		}
	}

	/// <summary>
	/// Covers a clip brush's footprint with boxes, because navigation areas cannot take its shape, so a diagonal brush
	/// no longer seals the passage beside it. A rectangular brush stays one box. Others are sliced across their shorter
	/// side with every slice inside the brush; the strips this leaves along a diagonal edge are narrower than the
	/// navigation radius, which already keeps units away from that wall.
	/// </summary>
	private static IEnumerable<BBox> BlockerBoxes( IReadOnlyList<Vector3> hull )
	{
		var bounds = BBox.FromPoints( hull );
		var slicedAlongX = bounds.Size.x <= bounds.Size.y;
		var end = slicedAlongX ? bounds.Maxs.x : bounds.Maxs.y;
		var start = slicedAlongX ? bounds.Mins.x : bounds.Mins.y;
		BBox Box( float first, float last, float low, float high ) => slicedAlongX
			? new BBox( new Vector3( first, low, bounds.Mins.z ), new Vector3( last, high, bounds.Maxs.z ) )
			: new BBox( new Vector3( low, first, bounds.Mins.z ), new Vector3( high, last, bounds.Maxs.z ) );

		(float First, float Last, float Low, float High)? run = null;
		for ( var position = start; position < end; position += BlockerSliceSize )
		{
			var next = Math.Min( position + BlockerSliceSize, end );
			var near = FootprintSpan( hull, slicedAlongX, position );
			var far = FootprintSpan( hull, slicedAlongX, next );
			var low = Math.Max( near.Low, far.Low );
			var high = Math.Min( near.High, far.High );
			if ( high <= low )
				continue;

			if ( run is { } open && Math.Abs( open.Low - low ) < 0.5f && Math.Abs( open.High - high ) < 0.5f && open.Last == position )
			{
				run = open with { Last = next };
				continue;
			}

			if ( run is { } finished )
				yield return Box( finished.First, finished.Last, finished.Low, finished.High );
			run = (position, next, low, high);
		}

		if ( run is { } last )
			yield return Box( last.First, last.Last, last.Low, last.High );
	}

	/// <summary>Finds where a line at one position enters and leaves the flat footprint of a convex hull.</summary>
	private static (float Low, float High) FootprintSpan( IReadOnlyList<Vector3> hull, bool slicedAlongX, float position )
	{
		var low = float.MaxValue;
		var high = float.MinValue;
		for ( var i = 0; i < hull.Count; i++ )
		{
			for ( var j = i; j < hull.Count; j++ )
			{
				var from = slicedAlongX ? hull[i].x : hull[i].y;
				var to = slicedAlongX ? hull[j].x : hull[j].y;
				if ( (from - position) * (to - position) > 0 )
					continue;

				var start = slicedAlongX ? hull[i].y : hull[i].x;
				var stop = slicedAlongX ? hull[j].y : hull[j].x;
				var crossing = from == to ? start : start + (stop - start) * (position - from) / (to - from);
				low = Math.Min( low, crossing );
				high = Math.Max( high, crossing );
			}
		}
		return (low, high);
	}

	/// <summary>Imports world grates and windows, including metal walkways, as static collision tagged "grate" that carries and stops units while sight and weapon traces skip it, so fences and railings do not block fire.</summary>
	private void AddGrates( LambdaWarsBsp bsp, LambdaWarsSkybox sky ) => AddBrushLayer( bsp, sky, BrushLayer.Grate, "grates", "grate" );

	/// <summary>Adds a world brush layer as tagged static collision, using a registered model because a replayed map reuses its saved scene and loads that model again by name.</summary>
	private GameObject AddBrushLayer( LambdaWarsBsp bsp, LambdaWarsSkybox sky, BrushLayer layer, string name, string tag, List<BBox> hullBounds = null )
	{
		using var reader = bsp.Open();
		var (textureInfos, textureData, textureNames) = ReadTextureTables( bsp, reader );
		if ( AddBrushCollision( bsp, reader, null, 0, budget, textureInfos, textureData, textureNames, sky, layer, hullBounds ) == 0 )
			return null;

		var model = Model.Load( $"mount://{Host.Ident}/{filename}.scene.{name}.vmdl" );
		if ( model is null || model.IsError )
			throw new InvalidDataException( $"The original {name} collision could not be loaded through its registered resource." );

		var brushes = new GameObject( true, name );
		brushes.Tags.Add( tag );
		var collider = brushes.Components.Create<ModelCollider>();
		collider.Model = model;
		collider.Static = true;
		return brushes;
	}

	/// <summary>Builds the collision-only model of one world brush layer for its registered resource.</summary>
	internal static Model BuildBrushLayer( LambdaWarsBsp bsp, LambdaWarsImportBudget budget, string resourcePath, BrushLayer layer )
	{
		using var reader = bsp.Open();
		var (textureInfos, textureData, textureNames) = ReadTextureTables( bsp, reader );
		var builder = Model.Builder.WithName( resourcePath );
		AddBrushCollision( bsp, reader, builder, 0, budget, textureInfos, textureData, textureNames, LambdaWarsSkybox.TryCreate( bsp ), layer );
		return builder.Create();
	}

	/// <summary>Detects a brush entity built only from grates and windows, such as the walkways across Canals.</summary>
	private static bool IsGrateModel( LambdaWarsBsp bsp, int modelIndex )
	{
		using var reader = bsp.Open();
		return ModelBrushes( bsp, reader, modelIndex ).All( brush =>
		{
			reader.BaseStream.Position = bsp.Lumps[18].Offset + brush * 12 + 8;
			return (reader.ReadInt32() & SolidContents) == 0;
		} );
	}

	private Model LoadRegisteredGeometry( int modelIndex )
	{
		var model = Model.Load( $"mount://{Host.Ident}/{filename}.scene.model_{modelIndex}.vmdl" );
		if ( model is null || model.IsError )
			throw new InvalidDataException( $"Original BSP model {modelIndex} could not be loaded through its registered resource." );
		return model;
	}

	internal static Model BuildGeometry( LambdaWarsMount host, LambdaWarsBsp bsp, LambdaWarsImportBudget budget, string resourcePath, int modelIndex, bool skyOnly = false )
	{
		var sky = modelIndex == 0 ? LambdaWarsSkybox.TryCreate( bsp ) : null;
		budget.SetStage( $"Reading geometry for BSP model {modelIndex}" );
		using var reader = bsp.Open();
		var vertices = bsp.ReadLumpRecords<Vector3>( 3 );
		var edges = bsp.ReadLumpRecords<LambdaWarsBsp.Edge>( 12 );
		var surfEdges = bsp.ReadLumpRecords<int>( 13 );
		var (textureInfos, textureData, textureNames) = ReadTextureTables( bsp, reader );

		reader.BaseStream.Position = bsp.Lumps[14].Offset + modelIndex * 48 + 40;
		var firstFace = reader.ReadInt32();
		var faceCount = reader.ReadInt32();
		var groups = new Dictionary<string, Geometry>( StringComparer.OrdinalIgnoreCase );
		var collisionVertices = new List<Vector3>();
		var collisionIndices = new List<int>();

		for ( var faceIndex = firstFace; faceIndex < firstFace + faceCount; faceIndex++ )
		{
			if ( modelIndex == 0 && (sky?.IsSkyFace( faceIndex ) ?? false) != skyOnly )
				continue;
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
			if ( !LambdaWarsBsp.IsDrawnSurface( name, info.Flags ) ) continue;

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
			var materialPath = $"materials/{pair.Key}.vmt";
			var material = skyOnly ? host.LoadSkyContentMaterial( materialPath ) : host.LoadContentMaterial( materialPath );
			var mesh = new Mesh( material );
			mesh.CreateVertexBuffer( pair.Value.Vertices.Count, pair.Value.Vertices );
			mesh.CreateIndexBuffer( pair.Value.Indices.Count, pair.Value.Indices );
			builder.AddMesh( mesh );
		}
		budget.SetStage( $"Preparing collision for BSP model {modelIndex}" );
		if ( !skyOnly && collisionIndices.Count > 0 )
			builder.AddCollisionMesh( collisionVertices, collisionIndices );
		if ( !skyOnly )
			AddBrushCollision( bsp, reader, builder, modelIndex, budget, textureInfos, textureData, textureNames, sky, modelIndex == 0 ? BrushLayer.Solid : BrushLayer.Entity );
		budget.SetStage( $"Creating BSP model {modelIndex}" );
		return builder.Create();
	}

	private static (LambdaWarsBsp.TextureInfo[] Infos, LambdaWarsBsp.TextureData[] Data, string[] Names) ReadTextureTables( LambdaWarsBsp bsp, BinaryReader reader )
	{
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

		return (textureInfos, textureData, textureNames);
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
		var multiblend = bsp.GetMultiBlendOffset( displacement );
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
				var point = Vector3.Lerp( Vector3.Lerp( c[0], c[1], v ), Vector3.Lerp( c[3], c[2], v ), u );
				reader.BaseStream.Position = bsp.Lumps[33].Offset + (firstVertex + y * side + x) * 20;
				point += LambdaWarsBsp.ReadVector( reader ) * reader.ReadSingle();
				var authoredAlpha = reader.ReadSingle();
				if ( !float.IsFinite( authoredAlpha ) )
					throw new InvalidDataException( "Displacement blend alpha must be finite." );
				var vertex = MakeVertex( point, normal, info, data, Math.Clamp( authoredAlpha / 255.0f, 0, 1 ) );
				if ( multiblend >= 0 )
				{
					reader.BaseStream.Position = bsp.Lumps[63].Offset + (multiblend + y * side + x) * 80;
					var weights = ReadBlendVector( reader );
					vertex.BlendAlpha = ReadBlendVector( reader );
					vertex.BlendColor1 = ReadBlendColor( reader, weights.x );
					vertex.BlendColor2 = ReadBlendColor( reader, weights.y );
					vertex.BlendColor3 = ReadBlendColor( reader, weights.z );
					vertex.BlendColor4 = ReadBlendColor( reader, weights.w );
				}
				group.Vertices.Add( vertex );
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
				var triangles = (x + y) % 2 == 0 ? new[] { a, b, d, a, d, c1 } : new[] { a, b, c1, b, d, c1 };
				group.Indices.AddRange( triangles.Select( i => i + index ) );
				collisionIndices.AddRange( triangles.Select( i => i + collision ) );
			}
		}
	}

	private static Vector4 ReadBlendVector( BinaryReader reader )
	{
		var value = new Vector4( reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle() );
		if ( !float.IsFinite( value.x ) || !float.IsFinite( value.y ) || !float.IsFinite( value.z ) || !float.IsFinite( value.w ) )
			throw new InvalidDataException( "Displacement blend values must be finite." );
		return value;
	}

	private static Vector4 ReadBlendColor( BinaryReader reader, float weight )
	{
		var color = LambdaWarsBsp.ReadVector( reader );
		if ( !float.IsFinite( color.x ) || !float.IsFinite( color.y ) || !float.IsFinite( color.z ) )
			throw new InvalidDataException( "Displacement blend colors must be finite." );
		return new Vector4( color, weight );
	}

	private static MapVertex MakeVertex( Vector3 point, Vector3 normal, LambdaWarsBsp.TextureInfo info, LambdaWarsBsp.TextureData data, float terrainBlend = 0 )
	{
		var u = new Vector3( info.U.x, info.U.y, info.U.z );
		var v = new Vector3( info.V.x, info.V.y, info.V.z );
		return new MapVertex( point, normal, new Vector4( u.Normal, 1 ), new Vector2( (point.Dot( u ) + info.U.w) / Math.Max( data.Width, 1 ), (point.Dot( v ) + info.V.w) / Math.Max( data.Height, 1 ) ), new Vector4( 1, 1, 1, terrainBlend ) );
	}

	private void AddEntityProps( LambdaWarsBsp bsp, LambdaWarsSkybox sky, bool skyOnly )
	{
		foreach ( var entity in bsp.Entities )
		{
			var classname = entity.GetValueOrDefault( "classname", "" );
			if ( classname is not ("prop_physics" or "prop_physics_override" or "prop_dynamic" or "prop_dynamic_override") || !entity.TryGetValue( "model", out var modelName ) )
				continue;
			var position = LambdaWarsBsp.Vector( entity.GetValueOrDefault( "origin", "0 0 0" ) );
			if ( (sky?.IsSkyPoint( position ) ?? false) != skyOnly )
				continue;

			var model = Host.LoadContentModel( modelName );
			budget.Check();
			if ( model is null || model.IsError )
				throw new InvalidDataException( $"Map prop could not be loaded: {modelName}" );

			var prop = new GameObject( true, modelName );
			prop.WorldPosition = position;
			prop.WorldRotation = LambdaWarsBsp.Angle( entity.GetValueOrDefault( "angles", "0 0 0" ) );
			ModelRenderer renderer;
			if ( skyOnly )
			{
				renderer = prop.Components.Create<ModelRenderer>();
			}
			else
			{
				var skinned = prop.Components.Create<SkinnedModelRenderer>();
				skinned.UseAnimGraph = false;
				renderer = skinned;
			}
			renderer.Model = model;
			if ( !skyOnly && entity.GetValueOrDefault( "solid", "6" ) != "0" )
			{
				var collider = prop.Components.Create<ModelCollider>();
				collider.Model = model;
				collider.Static = true;
			}
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

		if ( environment.TryGetValue( "_ambient", out var ambientColor ) )
		{
			var ambient = light.Components.Create<AmbientLight>();
			ambient.Color = ReadLightColor( ambientColor );
		}
	}

	private static Color ReadLightColor( string value )
	{
		var components = value.Split( ' ', StringSplitOptions.RemoveEmptyEntries ).Select( number => float.Parse( number, System.Globalization.CultureInfo.InvariantCulture ) ).ToArray();
		var brightness = components.Length > 3 ? components[3] / 200f : 1;
		return new Color( components[0] / 255f, components[1] / 255f, components[2] / 255f ) * brightness;
	}
	private void AddBrushEntities( LambdaWarsBsp bsp, LambdaWarsSkybox sky, bool skyOnly )
	{
		foreach ( var entity in bsp.Entities )
		{
			var classname = entity.GetValueOrDefault( "classname", "" );
			if ( classname is not ("func_brush" or "func_illusionary" or "func_wall" or "func_door" or "func_door_rotating") || !entity.TryGetValue( "model", out var modelName ) || !modelName.StartsWith( '*' ) )
				continue;
			var position = LambdaWarsBsp.Vector( entity.GetValueOrDefault( "origin", "0 0 0" ) );
			var bounds = bsp.EntityBounds( entity );
			if ( (sky?.IsSkyPoint( (bounds.Mins + bounds.Maxs) * 0.5f ) ?? false) != skyOnly )
				continue;

			var index = int.Parse( modelName[1..], System.Globalization.CultureInfo.InvariantCulture );
			var model = LoadRegisteredGeometry( index );
			var brush = new GameObject( true, entity.GetValueOrDefault( "targetname", classname ) );
			brush.WorldPosition = position;
			brush.WorldRotation = LambdaWarsBsp.Angle( entity.GetValueOrDefault( "angles", "0 0 0" ) );
			var renderer = brush.Components.Create<ModelRenderer>();
			renderer.Model = model;
			var disabled = classname == "func_brush" && entity.GetValueOrDefault( "StartDisabled", "0" ) != "0";
			renderer.Enabled = !disabled;
			var solidity = entity.GetValueOrDefault( "Solidity", "0" );
			if ( !skyOnly && classname != "func_illusionary" && (classname != "func_brush" || solidity != "1" && (!disabled || solidity == "2")) )
			{
				if ( IsGrateModel( bsp, index ) )
					brush.Tags.Add( "grate" );
				var collider = brush.Components.Create<ModelCollider>();
				collider.Model = model;
				collider.Static = true;
			}
		}
	}

	internal static HashSet<int> ModelBrushes( LambdaWarsBsp bsp, BinaryReader reader, int modelIndex )
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
	private void AddStaticProps( LambdaWarsStaticPropMetadata metadata, LambdaWarsSkybox sky, bool skyOnly )
	{
		var models = new Dictionary<string, Model>( StringComparer.OrdinalIgnoreCase );
		foreach ( var source in metadata.Props )
		{
			if ( (sky?.IsSkyProp( source, metadata ) ?? false) != skyOnly )
				continue;
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
			if ( model.BoneCount > 0 && !skyOnly )
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
				renderer.MaterialGroup = $"Skin {source.Skin}";
			}

			if ( !skyOnly && source.Solid != 0 )
			{
				var collider = prop.Components.Create<ModelCollider>();
				collider.Model = model;
				collider.Static = true;
			}
		}
	}

	/// <summary>Adds the Overrun maps' authored jump connections to navigation.</summary>
	private static void AddOffMeshLinks( LambdaWarsBsp bsp )
	{
		foreach ( var (start, end, oneWay) in bsp.OffMeshLinks() )
		{
			var connection = new GameObject( true, "recast_offmesh_connection" );
			connection.WorldPosition = start;
			var link = connection.Components.Create<NavMeshLink>();
			link.LocalStartPosition = Vector3.Zero;
			link.LocalEndPosition = end - start;
			link.IsBiDirectional = !oneWay;
			link.ConnectionRadius = 64;
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

	/// <summary>Imports visibly textured brush volumes of one layer while preserving their hidden closing and bevel planes. Invisible brushes stay out, except NPC clip, which is never drawn.</summary>
	private static int AddBrushCollision( LambdaWarsBsp bsp, BinaryReader reader, ModelBuilder builder, int modelIndex, LambdaWarsImportBudget budget, LambdaWarsBsp.TextureInfo[] textureInfos, LambdaWarsBsp.TextureData[] textureData, string[] textureNames, LambdaWarsSkybox sky, BrushLayer layer, List<BBox> hullBounds = null )
	{
		var added = 0;
		var npcClip = layer == BrushLayer.NpcClip;
		var planes = new (Vector3 Normal, float Distance)[bsp.Lumps[1].Length / 20];
		for ( var i = 0; i < planes.Length; i++ )
		{
			reader.BaseStream.Position = bsp.Lumps[1].Offset + i * 20;
			planes[i] = (LambdaWarsBsp.ReadVector( reader ), reader.ReadSingle());
		}

		foreach ( var brush in ModelBrushes( bsp, reader, modelIndex ) )
		{
			if ( modelIndex == 0 && (sky?.IsSkyBrush( brush ) ?? false) )
				continue;
			budget.Check();
			reader.BaseStream.Position = bsp.Lumps[18].Offset + brush * 12;
			var firstSide = reader.ReadInt32();
			var sideCount = reader.ReadInt32();
			var contents = reader.ReadInt32();
			if ( !InLayer( contents, layer ) || sideCount < 4 )
				continue;
			if ( sideCount > 1024 )
				throw new InvalidDataException( "A collision brush exceeds the current import work budget." );

			var sides = new (Vector3 Normal, float Distance)[sideCount];
			var walkableRamp = false;
			var hasVisibleSurface = false;
			for ( var side = 0; side < sideCount; side++ )
			{
				reader.BaseStream.Position = bsp.Lumps[19].Offset + (firstSide + side) * 8;
				sides[side] = planes[reader.ReadUInt16()];
				var infoIndex = reader.ReadInt16();
				reader.ReadInt16();
				var bevel = reader.ReadByte() != 0;
				walkableRamp |= !bevel && IsWalkableSlope( sides[side].Normal );
				if ( npcClip || infoIndex < 0 )
					continue;

				var info = textureInfos[infoIndex];
				var name = textureNames[textureData[info.Data].Name];
				if ( LambdaWarsBsp.IsDrawnSurface( name, info.Flags ) )
					hasVisibleSurface = true;
			}

			if ( !npcClip && !hasVisibleSurface )
				continue;

			var hull = LambdaWarsBrushHull.Build( sides, budget.Check );
			if ( hull.Count < 4 )
				continue;

			builder?.AddCollisionHull( hull );
			if ( !walkableRamp )
				hullBounds?.AddRange( BlockerBoxes( hull ) );
			added++;
		}
		return added;
	}

	private static bool InLayer( int contents, BrushLayer layer ) => layer switch
	{
		BrushLayer.Solid => (contents & SolidContents) != 0,
		BrushLayer.Grate => (contents & SolidContents) == 0 && (contents & GrateContents) != 0,
		BrushLayer.NpcClip => (contents & SolidContents) == 0 && (contents & NpcClipContents) != 0,
		_ => (contents & (SolidContents | GrateContents)) != 0
	};

	/// <summary>
	/// Detects an authored, non-bevel brush side sloped gently enough to walk on, however slight the slope. NPC clip brushes
	/// with such a side are ramps laid over stairs or terrain: units walk on them, so they keep their collision but must not
	/// carve a navigation hole, because a box up to their highest point would swallow all the ground below it.
	/// </summary>
	private static bool IsWalkableSlope( Vector3 normal ) => normal.z >= 0.7f && normal.z < 1;

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

		[VertexLayout.TexCoord( 3 )]
		public Vector4 BlendAlpha = Vector4.Zero;

		[VertexLayout.TexCoord( 4 )]
		public Vector4 BlendColor1 = new( 1, 1, 1, 1 );

		[VertexLayout.TexCoord( 5 )]
		public Vector4 BlendColor2 = new( 1, 1, 1, 0 );

		[VertexLayout.TexCoord( 6 )]
		public Vector4 BlendColor3 = new( 1, 1, 1, 0 );

		[VertexLayout.TexCoord( 7 )]
		public Vector4 BlendColor4 = new( 1, 1, 1, 0 );
	}

	private sealed class Geometry
	{
		internal List<MapVertex> Vertices { get; } = new();
		internal List<int> Indices { get; } = new();
	}
}

internal sealed record LambdaWarsPreparedMap( LambdaWarsBsp Bsp, LambdaWarsStaticPropMetadata StaticProps, long DecodedUploadBytes );

/// <summary>Rebuilds a registered BSP submodel after scene unload while sharing only live scene-import budgets.</summary>
internal sealed class LambdaWarsBspModelLoader( string filename, int modelIndex, bool skyOnly = false, LambdaWarsMapLoader.BrushLayer? brushLayer = null ) : ResourceLoader<LambdaWarsMount>
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
		return brushLayer is { } layer
			? LambdaWarsMapLoader.BuildBrushLayer( prepared.Bsp, budget, Path, layer )
			: LambdaWarsMapLoader.BuildGeometry( Host, prepared.Bsp, budget, Path, modelIndex, skyOnly );
	}
}
