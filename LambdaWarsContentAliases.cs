namespace Sandbox.Mounting.Source1;

/// <summary>Explicit replacements for moved or absent original resources; installed and map-embedded originals take precedence.</summary>
internal static class LambdaWarsContentAliases
{
	internal static readonly IReadOnlyDictionary<string, string> Paths = new Dictionary<string, string>
	{
		["sound/npc/stalker/stalker_laser_flesh.wav"] = "sound/npc/stalker/laser_flesh.wav",
		["materials/white.vtf"] = "materials/vgui/white.vtf",
		["materials/metal/metalgrate012_env.vtf"] = "materials/engine/defaultcubemap.vtf",
		["materials/metal/metalwall047a_normal.vtf"] = "materials/dev/flat_normal.vtf",
		["materials/models/shadertest/shader3_normal.vtf"] = "materials/dev/flat_normal.vtf",
		["materials/ground/snow_warp.vtf"] = "materials/dev/identitylightwarp.vtf",
		["materials/building_template/building_template015a.vmt"] = "materials/building_template/building_template015b.vmt",
		["materials/concrete/concretefloor016a.vmt"] = "materials/concrete/concretefloor015a.vmt",
		["materials/nature/water_coast01_beneath.vmt"] = "materials/lostcoast/nature/water_ati_beneath.vmt",
		["materials/nature/sewer_water001_bottom.vmt"] = "materials/nature/sewer_waterdarkfog.vmt",
		["materials/models/pg_props/pg_buildings/combine/comshieldwall.vmt"] = "materials/models/structures/combine/comshieldwall.vmt",
		["materials/models/pg_props/pg_buildings/combine/combine_fence01a.vmt"] = "materials/models/structures/combine/combine_fence01a.vmt"
	};

	internal static void Apply( BaseMount mount )
	{
		foreach ( var pair in Paths )
			mount.AddContentAlias( pair.Key, pair.Value );
	}
}
