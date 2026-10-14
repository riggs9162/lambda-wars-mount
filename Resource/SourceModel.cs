using System;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed class SourceModel( BaseMount mount, string path, (MdlFile Mdl, VtxFile Vtx, PhyFile Physics) source,
	Transform[] referencePose = null )
{
	internal (MdlFile Mdl, VtxFile Vtx, PhyFile Physics) Source { get; } = source;
	internal Transform[] ReferencePose { get; } = referencePose;

	internal Model Build( string name ) =>
		new SourceModelBuilder( mount, path ).BuildModel( Source.Mdl, Source.Vtx, Source.Physics, name, ReferencePose );

	internal static SourceModel Read( BaseMount mount, string filename )
	{
		filename = filename.Replace( '\\', '/' );
		using var mdlStream = mount.OpenContentFile( filename );
		var mdl = new MdlFile( mdlStream );
		PhyFile physics = null;
		var phyPath = System.IO.Path.ChangeExtension( filename, ".phy" );
		if ( mount.TryOpenContentFile( phyPath, out var phyStream ) )
		{
			using ( phyStream )
			{
				try
				{
					physics = new PhyFile( phyStream, phyPath, mdl.Checksum );
				}
				catch ( Exception e ) when ( e is InvalidDataException or NotSupportedException )
				{
					Log.Trace( $"{filename}: physics was not imported: {e.Message}" );
				}
			}
		}
		Transform[] referencePose = null;
		if ( physics is { Solids.Count: 1, Constraints.Count: 0 } && !mdl.IsStaticProp )
		{
			try
			{
				referencePose = mdl.ReadPhysicsReferencePose( mdlStream, path =>
					mount.OpenContentFile( path.StartsWith( "models/", StringComparison.OrdinalIgnoreCase )
						? path : filename[..(filename.LastIndexOf( '/' ) + 1)] + path ) );
			}
			catch ( Exception e ) when ( e is InvalidDataException or NotSupportedException or FileNotFoundException )
			{
				Log.Trace( $"{filename}: rigid collision reference alignment is unavailable; authored bind pose retained: {e.Message}" );
			}
		}
		if ( mdl.TotalVertices == 0 )
			return new SourceModel( mount, filename, (mdl, null, physics), referencePose );
		using var vvdStream = mount.OpenModelCompanion( filename, ".vvd" );
		using var vtxStream = mount.OpenModelCompanion( filename, ".dx90.vtx" );
		var vvd = new VvdFile( vvdStream );
		var vtx = new VtxFile( vtxStream, mdl, vvd, swarmLayout: mount is LambdaWarsMount );
		if ( mount is LambdaWarsMount )
			LambdaWarsModelInfo.ValidateGeometry( mdl, vtx );
		return new SourceModel( mount, filename, (mdl, vtx, physics), referencePose );
	}
}
