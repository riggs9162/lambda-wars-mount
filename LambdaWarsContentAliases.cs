namespace Sandbox.Mounting.Source1;

/// <summary>Explicit repairs for original references whose files moved within the installed game.</summary>
internal static class LambdaWarsContentAliases
{
	internal static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>
	{
		["materials/models/pg_props/pg_buildings/combine/comshieldwall.vmt"] = "materials/models/structures/combine/comshieldwall.vmt",
		["materials/models/pg_props/pg_buildings/combine/combine_fence01a.vmt"] = "materials/models/structures/combine/combine_fence01a.vmt"
	};

	internal static void Apply( BaseMount mount )
	{
		foreach ( var pair in Paths )
			mount.AddContentAlias( pair.Key, pair.Value );
	}
}
