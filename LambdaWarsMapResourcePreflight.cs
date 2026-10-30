using System;
using System.Text;
using System.Text.Json;

namespace Sandbox.Mounting.Source1;

internal sealed record LambdaWarsUploadTextureEstimate( string Path, int Width, int Height, int Frames, int Faces, int Mips, int BytesPerPixel, long UploadBytes );

internal sealed class LambdaWarsMapResourceReport
{
	public bool IsComplete { get; init; }
	public bool IsValid => IsComplete && Failures.Count == 0;
	public int BrushModelCount { get; init; }
	public int BrushEntityCount { get; init; }
	public int RenderedFaceCount { get; init; }
	public long GeometryVertexCount { get; init; }
	public long GeometryTriangleCount { get; init; }
	public int StaticPropCount { get; init; }
	public int StaticModelCount { get; init; }
	public int EntityPropCount { get; init; }
	public int EntityModelCount { get; init; }
	public long ModelVertexCount { get; init; }
	public int MaterialCount { get; init; }
	public int TextureCount => Textures.Count;
	public long DecodedUploadBytes { get; init; }
	public IReadOnlyList<LambdaWarsUploadTextureEstimate> Textures { get; init; } = [];
	public IReadOnlyList<LambdaWarsDependencyFailure> Failures { get; init; } = [];
}

/// <summary>Validates rendered BSP, brush and prop dependencies and estimates the converter's authored texture uploads without creating GPU resources.</summary>
internal static class LambdaWarsMapResourcePreflight
{
	internal static LambdaWarsMapResourceReport Inspect( BaseMount mount, LambdaWarsBsp bsp, LambdaWarsStaticPropPreflightReport staticProps, Action budgetCheck = null )
	{
		ArgumentNullException.ThrowIfNull( mount );
		ArgumentNullException.ThrowIfNull( bsp );
		ArgumentNullException.ThrowIfNull( staticProps );
		var state = new Inspection( mount, budgetCheck );
		state.Failures.AddRange( staticProps.Failures );
		state.ModelVertices = staticProps.VertexCount;
		state.StaticProps = staticProps.PropCount;
		state.StaticModels = staticProps.ModelCount;
		try
		{
			using var propSource = bsp.Open();
			state.SeedStaticModels( LambdaWarsStaticPropReader.Read( propSource.BaseStream ) );
			state.ReadGeometry( bsp );
			foreach ( var material in staticProps.MaterialPaths )
				state.ReadMaterial( material );

			state.ReadEntityProps( bsp );
			state.Complete = staticProps.IsComplete;
		}
		catch ( BudgetException exception )
		{
			state.Failures.Add( new( "", "Budget", exception.Message ) );
		}
		catch ( Exception exception ) when ( exception is IOException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException )
		{
			state.Failures.Add( new( "", "Map metadata", exception.Message ) );
		}

		return state.Report();
	}

	internal static LambdaWarsUploadTextureEstimate EstimateUpload( string path, VtfHeader header, int frames, int faces, int authoredMips )
	{
		if ( TextureLoader.GetImageFormat( header.HiResFormat ).Format == ImageFormat.None )
			throw new NotSupportedException( $"Texture format {header.HiResFormat} is unsupported: {path}" );

		if ( header.Width < 1 || header.Height < 1 || header.Width > 4096 || header.Height > 4096 || frames < 1 || frames > 256
			|| faces is not (1 or 6 or 7) || authoredMips < 1 || authoredMips > 1 + (int)Math.Floor( Math.Log2( Math.Max( header.Width, header.Height ) ) ) )
			throw new InvalidDataException( $"Texture dimensions exceed upload estimate limits: {path}" );

		var mips = (header.Flags & VtfFlags.NOMIP) != 0 ? 1 : authoredMips;
		var uploadedFaces = Math.Min( faces, 6 );
		var stride = header.HiResFormat switch
		{
			VtfFormat.RGBA16161616 or VtfFormat.RGBA16161616F => 8,
			VtfFormat.RGB323232F or VtfFormat.RGBA32323232F => 16,
			_ => 4
		};
		long pixels = 0;
		for ( var mip = 0; mip < mips; mip++ )
			pixels = checked(pixels + (long)Math.Max( 1, header.Width >> mip ) * Math.Max( 1, header.Height >> mip));

		return new( Normalize( path ), header.Width, header.Height, frames, uploadedFaces, mips, stride, checked(pixels * frames * uploadedFaces * stride) );
	}

	private static string Normalize( string path ) => path.Replace( '\\', '/' ).TrimStart( '/' ).ToLowerInvariant();

	private sealed class BudgetException( string message ) : Exception( message );

	private sealed class Inspection( BaseMount mount, Action budgetCheck )
	{
		internal readonly List<LambdaWarsDependencyFailure> Failures = [];
		private readonly HashSet<string> materials = new( StringComparer.OrdinalIgnoreCase );
		private readonly HashSet<string> attemptedTextures = new( StringComparer.OrdinalIgnoreCase );
		private readonly HashSet<string> modelPaths = new( StringComparer.OrdinalIgnoreCase );
		private readonly Dictionary<string, LambdaWarsUploadTextureEstimate> textures = new( StringComparer.OrdinalIgnoreCase );
		internal bool Complete;
		internal int BrushModels;
		internal int BrushEntities;
		internal int Faces;
		internal long Vertices;
		internal long Triangles;
		internal int StaticProps;
		internal int StaticModels;
		internal int EntityProps;
		internal int EntityModels;
		internal long ModelVertices;

		internal void SeedStaticModels( LambdaWarsStaticPropMetadata metadata )
		{
			foreach ( var prop in metadata.Props )
				modelPaths.Add( Normalize( prop.ModelPath ) );
		}

		private void Check()
		{
			try
			{
				budgetCheck?.Invoke();
			}
			catch ( Exception exception ) when ( exception is IOException or InvalidDataException or OperationCanceledException )
			{
				throw new BudgetException( exception.Message );
			}
		}

		internal void ReadGeometry( LambdaWarsBsp bsp )
		{
			using var reader = bsp.Open();
			RequireStride( bsp, 7, 56 );
			RequireStride( bsp, 6, 72 );
			RequireStride( bsp, 2, 32 );
			RequireStride( bsp, 14, 48 );
			RequireStride( bsp, 44, 4 );
			RequireStride( bsp, 26, 176 );
			RequireStride( bsp, 63, 80 );
			var names = ReadTextureNames( bsp, reader );
			var modelIndices = new HashSet<int> { 0 };
			foreach ( var entity in bsp.Entities )
			{
				if ( entity.GetValueOrDefault( "classname", "" ) is not ("func_brush" or "func_illusionary" or "func_wall" or "func_door" or "func_door_rotating") )
					continue;

				if ( !entity.TryGetValue( "model", out var model ) || !model.StartsWith( '*' ) )
					continue;

				if ( !int.TryParse( model[1..], out var index ) || index < 0 || index >= bsp.Lumps[14].Length / 48 )
					throw new InvalidDataException( $"Invalid brush model reference: {model}" );

				modelIndices.Add( index );
				BrushEntities++;
			}
			BrushModels = modelIndices.Count;
			foreach ( var model in modelIndices )
			{
				Check();
				reader.BaseStream.Position = bsp.Lumps[14].Offset + model * 48 + 40;
				var first = reader.ReadInt32();
				var count = reader.ReadInt32();
				if ( first < 0 || count < 0 || (long)first + count > bsp.Lumps[7].Length / 56 )
					throw new InvalidDataException( "A brush model references faces outside the BSP." );

				for ( var face = first; face < first + count; face++ )
				{
					if ( (face & 127) == 0 )
						Check();

					reader.BaseStream.Position = bsp.Lumps[7].Offset + face * 56 + 8;
					var edges = reader.ReadInt16();
					var infoIndex = reader.ReadInt16();
					var displacement = reader.ReadInt16();
					if ( infoIndex < 0 || edges < 3 )
						continue;

					if ( infoIndex >= bsp.Lumps[6].Length / 72 )
						throw new InvalidDataException( "A face references an invalid texture-info record." );

					reader.BaseStream.Position = bsp.Lumps[6].Offset + infoIndex * 72 + 64;
					var flags = reader.ReadInt32();
					var data = reader.ReadInt32();
					if ( data < 0 || data >= bsp.Lumps[2].Length / 32 )
						throw new InvalidDataException( "A face references an invalid texture-data record." );

					reader.BaseStream.Position = bsp.Lumps[2].Offset + data * 32 + 12;
					var name = reader.ReadInt32();
					if ( name < 0 || name >= names.Length )
						throw new InvalidDataException( "A face references an invalid texture name." );

					if ( names[name].StartsWith( "tools/", StringComparison.OrdinalIgnoreCase ) || (flags & 0x84) != 0 )
						continue;

					Faces++;
					if ( displacement >= 0 )
					{
						if ( displacement >= bsp.Lumps[26].Length / 176 || edges != 4 )
							throw new InvalidDataException( "A face references invalid displacement geometry." );

						reader.BaseStream.Position = bsp.Lumps[26].Offset + displacement * 176 + 20;
						var power = reader.ReadInt32();
						if ( power is < 2 or > 4 )
							throw new InvalidDataException( "Invalid displacement power." );

						var cells = 1 << power;
						Vertices = checked(Vertices + (long)(cells + 1) * (cells + 1));
						Triangles = checked(Triangles + 2L * cells * cells);
					}
					else
					{
						Vertices = checked(Vertices + edges);
						Triangles = checked(Triangles + edges - 2);
					}
					ReadMaterial( $"materials/{names[name]}.vmt" );
				}
			}
		}

		private static void RequireStride( LambdaWarsBsp bsp, int lump, int stride )
		{
			if ( bsp.Lumps[lump].Length % stride != 0 || bsp.Lumps[lump].FourCc != 0 )
				throw new InvalidDataException( $"Unsupported or truncated BSP lump {lump}." );
		}

		private static string[] ReadTextureNames( LambdaWarsBsp bsp, BinaryReader reader )
		{
			var bytes = bsp.GetLump( 43 );
			var names = new string[bsp.Lumps[44].Length / 4];
			for ( var index = 0; index < names.Length; index++ )
			{
				reader.BaseStream.Position = bsp.Lumps[44].Offset + index * 4;
				var offset = reader.ReadInt32();
				if ( offset < 0 || offset >= bytes.Length )
					throw new InvalidDataException( "A BSP texture-string offset is invalid." );

				var end = Array.IndexOf( bytes, (byte)0, offset );
				if ( end == -1 )
					throw new InvalidDataException( "A BSP texture string is unterminated." );

				names[index] = Encoding.UTF8.GetString( bytes, offset, end - offset );
			}
			return names;
		}

		internal void ReadMaterial( string path )
		{
			path = Normalize( VmtFile.NormalizePath( path, ".vmt" ) );
			if ( !materials.Add( path ) )
				return;

			Check();
			try
			{
				var material = VmtFile.Load( path, mount.OpenContentFile );
				foreach ( var pair in VmtFile.SelectParameters( material ) )
				{
					if ( !TextureParameters.Contains( pair.Key, StringComparer.OrdinalIgnoreCase ) || string.IsNullOrWhiteSpace( pair.Value )
						|| pair.Value.StartsWith( "_rt_", StringComparison.OrdinalIgnoreCase ) || pair.Value.Equals( "env_cubemap", StringComparison.OrdinalIgnoreCase ) )
						continue;

					var texture = VmtFile.NormalizePath( pair.Value, ".vtf" );
					if ( pair.Key.Equals( "$envmap", StringComparison.OrdinalIgnoreCase ) && !texture.EndsWith( ".hdr.vtf", StringComparison.OrdinalIgnoreCase ) )
					{
						var hdr = texture[..^4] + ".hdr.vtf";
						if ( mount.GetContentPath( hdr ) is not null )
							texture = hdr;
						else if ( mount.GetContentPath( texture ) is null )
							texture = mount.GetContentPath( "materials/engine/defaultcubemap.hdr.vtf" ) is not null
								? "materials/engine/defaultcubemap.hdr.vtf" : "materials/engine/defaultcubemap.vtf";
					}
					ReadTexture( texture );
				}
			}
			catch ( Exception exception ) when ( exception is IOException or InvalidDataException or NotSupportedException or ArgumentException )
			{
				Failures.Add( new( path, "Material", exception.Message ) );
			}
		}

		internal void ReadTexture( string path )
		{
			path = Normalize( path );
			if ( !attemptedTextures.Add( path ) )
				return;

			Check();
			try
			{
				using var source = mount.OpenContentFile( path );
				var header = new VtfFile( source, onlyHeader: true );
				TextureLoader.ValidateSize( header, path, null );
				source.Position = 0;
				var texture = VtfFile.InspectTextureStream( source );
				textures.Add( path, EstimateUpload( path, texture.Header, texture.FrameCount, texture.FaceCount, texture.MipmapCount ) );
			}
			catch ( Exception exception ) when ( exception is IOException or InvalidDataException or NotSupportedException or ArgumentException or OverflowException )
			{
				Failures.Add( new( path, "Texture", exception.Message ) );
			}
		}

		internal void ReadEntityProps( LambdaWarsBsp bsp )
		{
			var models = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
			foreach ( var entity in bsp.Entities )
			{
				if ( entity.GetValueOrDefault( "classname", "" ) is not ("prop_physics" or "prop_physics_override" or "prop_dynamic" or "prop_dynamic_override")
					|| !entity.TryGetValue( "model", out var model ) )
					continue;

				EntityProps++;
				if ( !models.Add( Normalize( model ) ) )
					continue;

				Check();
				var report = LambdaWarsModelInfo.Inspect( mount, model );
				if ( !report.IsValid )
				{
					Failures.Add( new( model, "Entity model", report.Error ) );
					continue;
				}
				if ( modelPaths.Add( Normalize( model ) ) )
					ModelVertices = checked(ModelVertices + report.TotalVertices);
				foreach ( var material in report.Materials )
					ReadMaterial( JsonSerializer.SerializeToElement( material ).GetProperty( "Path" ).GetString() );
			}
			EntityModels = models.Count;
		}

		internal LambdaWarsMapResourceReport Report() => new()
		{
			IsComplete = Complete,
			BrushModelCount = BrushModels,
			BrushEntityCount = BrushEntities,
			RenderedFaceCount = Faces,
			GeometryVertexCount = Vertices,
			GeometryTriangleCount = Triangles,
			StaticPropCount = StaticProps,
			StaticModelCount = StaticModels,
			EntityPropCount = EntityProps,
			EntityModelCount = EntityModels,
			ModelVertexCount = ModelVertices,
			MaterialCount = materials.Count,
			DecodedUploadBytes = textures.Values.Sum( texture => texture.UploadBytes ),
			Textures = textures.Values.OrderBy( texture => texture.Path, StringComparer.OrdinalIgnoreCase ).ToArray(),
			Failures = Failures.ToArray()
		};
	}

	private static readonly string[] TextureParameters =
	[
		"$basetexture", "$basetexture2", "$basetexture3", "$basetexture4", "$bumpmap", "$bumpmap2", "$detail", "$envmap", "$envmapmask",
		"$selfillummask", "$phongexponenttexture", "$lightwarptexture", "$iris", "$corneatexture", "$ambientoccltexture",
		"$texture2", "$normalmap", "$normalmap2", "$refracttinttexture", "$blendmodulatetexture",
		"$spectexture", "$spectexture2", "$spectexture3", "$spectexture4"
	];
}
