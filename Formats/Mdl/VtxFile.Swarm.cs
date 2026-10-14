using System;
using System.IO;

namespace Sandbox.Mounting.Source1;

internal sealed partial class VtxFile
{
	private static void ReadStripGroups( Stream stream, int count, bool swarmLayout, Action<StripGroupHeader> handler )
	{
		if ( swarmLayout )
			LumpReader<SwarmStripGroupHeader>.ReadLumpFromStream( stream, count, item => handler( item.Header ) );
		else
			LumpReader<StripGroupHeader>.ReadLumpFromStream( stream, count, handler );
	}

	private static void ReadStrips( Stream stream, int count, bool swarmLayout, Action<StripHeader> handler )
	{
		if ( swarmLayout )
			LumpReader<SwarmStripHeader>.ReadLumpFromStream( stream, count, item => handler( item.Header ) );
		else
			LumpReader<StripHeader>.ReadLumpFromStream( stream, count, handler );
	}
}
