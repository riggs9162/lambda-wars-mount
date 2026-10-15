using System;
using System.IO;
using Sandbox.Mounting;

namespace Sandbox.Mounting.Source1;

public abstract partial class BaseMount
{
	/// <summary>Registers original logical sound scripts lazily so game cues retain random waves, pitch, volume and distance falloff.</summary>
	private void MountSoundEvents(MountContext context, FileIndex files)
	{
		const string manifest = "scripts/game_sounds_manifest.txt";
		if (!files.Files.ContainsKey(manifest))
			return;

		var registered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var path in ScriptManifest.Read(Read(manifest), manifest, "game_sounds_manifest", "precache_file", "declare_file", "preload_file"))
		{
			if (!files.Files.ContainsKey(path))
				continue;

			try
			{
				foreach (var root in ScriptManifest.Parse(Read(path), path, message => Log.Trace(message)))
				{
					if (!root.IsBlock || !registered.Add(root.Name))
						continue;

					context.Add(ResourceType.GameResource, $"sounds/events/{Uri.EscapeDataString(root.Name.ToLowerInvariant())}.sound",
						new SourceScriptSoundLoader(root, path));
					if (root.Children.Any(entry => entry.Name.Equals("wave", StringComparison.OrdinalIgnoreCase) && entry.Value.Contains('^')
						|| entry.IsBlock && entry.Children.Any(wave => wave.Value?.Contains('^') == true)))
						context.Add(ResourceType.GameResource, $"sounds/events/{Uri.EscapeDataString(root.Name.ToLowerInvariant())}.distant.sound",
							new SourceScriptSoundLoader(root, path, true));
					if (root.Children.Any(entry => entry.Name.Equals("wave", StringComparison.OrdinalIgnoreCase) && entry.Value.Contains('<')
						|| entry.IsBlock && entry.Children.Any(wave => wave.Value?.Contains('<') == true)))
						context.Add(ResourceType.GameResource, $"sounds/events/{Uri.EscapeDataString(root.Name.ToLowerInvariant())}.rear.sound",
							new SourceScriptSoundLoader(root, path, true));
				}
			}
			catch (Exception exception) when (exception is IOException or InvalidDataException or NotSupportedException)
			{
				Log.Trace($"{Title}: sound script {path} was not registered: {exception.Message}");
			}
		}

		string Read(string path)
		{
			using var stream = files.Files[path].Open();
			using var reader = new StreamReader(stream);
			return reader.ReadToEnd();
		}
	}
}

internal sealed class SourceScriptSoundLoader(VmtNode root, string sourcePath, bool distant = false) : ResourceLoader<BaseMount>
{
	protected override object Load()
	{
		var definition = Host.ResolveSurfaceSound(SourceSoundDefinition.Parse(root, sourcePath));
		return definition is null ? throw new InvalidDataException($"{root.Name}: no original sound variants are installed.")
			: SourceSoundEvents.Create(Host, definition, distant);
	}
}
