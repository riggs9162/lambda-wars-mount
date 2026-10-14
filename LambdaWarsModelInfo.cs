using System;
using System.IO;

namespace Sandbox.Mounting.Source1;

/// <summary>Checks original model geometry and texture dependencies without creating GPU resources.</summary>
internal sealed class LambdaWarsModelReport
{
	public string Path { get; init; }
	public bool IsValid { get; init; }
	public string Error { get; init; } = "";
	public int BoneCount { get; init; }
	public int TotalVertices { get; init; }
	public int LodCount { get; init; }
	public object[] Materials { get; init; } = [];
	public object[] Textures { get; init; } = [];
}

internal static class LambdaWarsModelInfo
{
	internal static LambdaWarsModelReport Inspect( BaseMount mount, string filename )
	{
		try
		{
			var source = mount.ReadSourceModel( filename );
			var (mdl, vtx, _) = source.Source;
			ValidateGeometry( mdl, vtx );
			SourceModelBuilder.ValidateSkeleton( mdl, source.ReferencePose );
			var materials = new List<object>();
			var textures = new Dictionary<string, object>( StringComparer.OrdinalIgnoreCase );
			var materialNames = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
			for ( var slot = 0; slot < mdl.MaterialCount; slot++ )
			{
				materialNames.Add( mdl.GetMaterialName( slot ) );
				for ( var lod = 0; lod < (vtx?.RenderLodCount ?? 0); lod++ )
				{
					var replacement = vtx.GetMaterialReplacement( lod, slot );
					if ( !string.IsNullOrWhiteSpace( replacement ) )
						materialNames.Add( replacement );
				}
			}
			foreach ( var name in materialNames )
			{
				var path = FindMaterial( mount, mdl, name );
				var material = VmtFile.Load( path, mount.OpenContentFile );
				materials.Add( new { Path = path, Shader = material.ShaderName } );
				foreach ( var pair in VmtFile.SelectParameters( material ) )
				{
					if ( !TextureParameters.Contains( pair.Key, StringComparer.OrdinalIgnoreCase ) )
						continue;
					if ( string.IsNullOrWhiteSpace( pair.Value ) || pair.Value.StartsWith( "_rt_", StringComparison.OrdinalIgnoreCase )
						|| pair.Value.Equals( "env_cubemap", StringComparison.OrdinalIgnoreCase ) )
						continue;
					var texturePath = VmtFile.NormalizePath( pair.Value, ".vtf" );
					if ( textures.ContainsKey( texturePath ) )
						continue;
					using var stream = mount.OpenContentFile( texturePath );
					var metadata = new VtfFile( stream, onlyHeader: true );
					TextureLoader.ValidateSize( metadata, texturePath, null );
					stream.Position = 0;
					var texture = VtfFile.FromTextureStream( stream, null );
					textures.Add( texturePath, new
					{
						Path = texturePath,
						texture.Header.Width,
						texture.Header.Height,
						Format = texture.Header.HiResFormat.ToString(),
						texture.FrameCount,
						texture.MipmapCount
					} );
				}
			}
			return new LambdaWarsModelReport
			{
				Path = filename,
				IsValid = true,
				Error = "",
				BoneCount = mdl.BoneCount,
				TotalVertices = mdl.TotalVertices,
				LodCount = vtx?.RenderLodCount ?? 0,
				Materials = materials.ToArray(),
				Textures = textures.Values.ToArray()
			};
		}
		catch ( Exception exception ) when ( exception is IOException or InvalidDataException or NotSupportedException or ArgumentException )
		{
			return new LambdaWarsModelReport { Path = filename, IsValid = false, Error = exception.Message };
		}
	}

	private static readonly string[] TextureParameters =
	[
		"$basetexture", "$basetexture2", "$bumpmap", "$bumpmap2", "$detail", "$envmap", "$envmapmask",
		"$selfillummask", "$phongexponenttexture", "$lightwarptexture", "$iris", "$corneatexture", "$ambientoccltexture",
		"$texture2", "$normalmap", "$normalmap2", "$refracttinttexture"
	];

	private static string FindMaterial( BaseMount mount, MdlFile mdl, string name )
	{
		for ( var index = 0; index < mdl.MaterialPathCount; index++ )
		{
			var path = VmtFile.NormalizePath( mdl.GetMaterialFullPath( name, index ), ".vmt" );
			if ( mount.GetContentPath( path ) is not null )
				return path;
		}
		var fallback = VmtFile.NormalizePath( name, ".vmt" );
		if ( mount.GetContentPath( fallback ) is not null )
			return fallback;
		throw new FileNotFoundException( $"Original material '{name}' is missing from the model's search paths.", fallback );
	}

	internal static void ValidateGeometry( MdlFile mdl, VtxFile vtx )
	{
		if ( mdl.BoneCount > 256 || mdl.TotalVertices > 250000 || (mdl.BoneCount == 0 && mdl.TotalVertices > 0) )
			throw new InvalidDataException( "The original model exceeds the current bone or vertex import limit." );
		if ( vtx is null )
			return;
		for ( var body = 0; body < mdl.BodyPartCount; body++ )
		for ( var modelIndex = 0; modelIndex < mdl.GetBodyPart( body ).NumModels; modelIndex++ )
		for ( var lod = 0; lod < vtx.RenderLodCount; lod++ )
		for ( var meshIndex = 0; meshIndex < mdl.GetModel( body, modelIndex ).NumMeshes; meshIndex++ )
		{
			var mesh = mdl.GetMesh( body, modelIndex, meshIndex );
			if ( mesh.NumVertices == 0 )
				continue;
			var vertices = vtx.GetVertices( body, modelIndex, lod, meshIndex );
			var tangents = vtx.GetTangents( body, modelIndex, lod, meshIndex );
			var indices = new int[vtx.GetIndexCount( body, modelIndex, lod, meshIndex )];
			vtx.GetIndices( body, modelIndex, lod, meshIndex, indices );
			var availableVertices = Math.Min( vertices.Length, Math.Min( mesh.NumVertices, ushort.MaxValue + 1 ) );
			if ( indices.Length % 3 != 0 || indices.Any( index => index < 0 || index >= availableVertices ) )
				throw new InvalidDataException( "Original model contains invalid triangle indices." );
			for ( var index = 0; index < vertices.Length; index++ )
			{
				var vertex = vertices[index];
				if ( !Finite( vertex.Position ) || !Finite( vertex.Normal )
					|| !float.IsFinite( vertex.TexCoordX ) || !float.IsFinite( vertex.TexCoordY ) )
					throw new InvalidDataException( "Original model contains a non-finite vertex." );
				SourceModelBuilder.ValidateBlendWeights( vertex.BoneWeights, mdl.BoneCount );
				if ( !tangents.IsEmpty )
				{
					var tangent = tangents[index];
					if ( !float.IsFinite( tangent.x ) || !float.IsFinite( tangent.y ) || !float.IsFinite( tangent.z ) || !float.IsFinite( tangent.w ) )
						throw new InvalidDataException( "Original model contains a non-finite tangent." );
				}
			}
		}
	}

	private static bool Finite( Vector3 value ) => float.IsFinite( value.x ) && float.IsFinite( value.y ) && float.IsFinite( value.z );
}

internal sealed class LambdaWarsModelInfoLoader( string filename ) : ResourceLoader<LambdaWarsMount>
{
	protected override object Load() => Json.Serialize( LambdaWarsModelInfo.Inspect( Host, filename ) );
}
