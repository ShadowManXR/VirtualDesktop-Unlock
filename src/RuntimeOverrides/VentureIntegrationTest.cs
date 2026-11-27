using VirtualDesktop.Unlock;
using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Threading;

internal static class VentureIntegrationTest
{
    private const BindingFlags All = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    private static void Check(bool valid, string label)
    {
        if (!valid) throw new Exception(label);
        Console.WriteLine("PASS " + label);
    }

    private static void Main(string[] args)
    {
        if (args.Length == 0)
        {
            string childMarkerPath = Environment.GetEnvironmentVariable("VD_CUSTOM_TEST_CHILD_MARKER");
            if (String.IsNullOrEmpty(childMarkerPath)) { Thread.Sleep(30000); return; }
            using (var child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--wait") { UseShellExecute = false, CreateNoWindow = true }))
                File.WriteAllText(childMarkerPath, child.Id.ToString());
            return;
        }
        if (args.Length == 1 && args[0] == "--wait") { Thread.Sleep(30000); return; }
        if (args.Length == 2 && args[0] == "--job-parent")
        {
            var child = Process.Start(new ProcessStartInfo(Assembly.GetExecutingAssembly().Location, "--wait") { UseShellExecute = false });
            File.WriteAllText(args[1], child.Id.ToString());
            child.WaitForExit();
            return;
        }
        string root = args[2];
        Directory.CreateDirectory(root);
        var assembly = Assembly.LoadFrom(args[0]);
        Module module = assembly.ManifestModule;
        StreamerProfile.Field(module, 0x04014843).SetValue(null, root);
        StreamerProfile.Field(module, 0x04014844).SetValue(null, root);
        var runtime = Assembly.LoadFrom(args[1]).GetType("VirtualDesktop.LibraryOverrides.VentureRuntime", true);
        runtime.GetField("customCatalogPath", All).SetValue(null, Path.Combine(root, "CustomGames.xml"));
        Type preferencesType = runtime.Assembly.GetType("VirtualDesktop.Unlock.VenturePreferences", true);
        object preferences = Activator.CreateInstance(preferencesType, Path.Combine(root, "Venture.enabled"));
        var install = runtime.GetMethod("Install", All);
        var installArgs = new object[] { assembly, new Action<string>(Console.WriteLine), preferences };
        install.Invoke(null, installArgs);
        Check(runtime.GetField("adapter", All).GetValue(null) == null, "Default off leaves Streamer's game-list hook untouched");
        string initialCustomCatalog = Path.Combine(root, "CustomGames.xml");
        File.WriteAllText(initialCustomCatalog, "<CustomGames version=\"1\" />");
        install.Invoke(null, installArgs);
        Check(runtime.GetField("adapter", All).GetValue(null) != null,
            "A custom game catalog installs the dashboard hook while Venture is disabled");
        File.Delete(initialCustomCatalog);
        preferencesType.GetMethod("SetEnabled").Invoke(preferences, new object[] { true });

        Type collectionType = assembly.GetType("VirtualDesktop.Streamer.GameCollection", true);
        object collection = Activator.CreateInstance(collectionType, true);
        Type itemType = collectionType.BaseType.GetGenericArguments()[1];
        string launcher = Path.Combine(root, "venture.exe");
        File.WriteAllText(launcher, "Inert placeholder, never launched.");
        string game = Path.Combine(root, "games", "com.example.alpha", "42");
        Directory.CreateDirectory(game);
        File.WriteAllText(Path.Combine(root, "library.json"), "{\"games\":[{\"id\":\"12345\",\"name\":\"Alpha Test Game\"}]}");
        string cachedArt = Path.Combine(root, "cache", "art");
        Directory.CreateDirectory(cachedArt);
        File.WriteAllText(Path.Combine(cachedArt, "12345.image"), "Unsupported artwork; use a title card.");
        File.WriteAllText(Path.Combine(game, "plan.json"),
            "{\"package\":\"com.example.alpha\",\"appId\":\"12345\",\"files\":[{\"kind\":\"apk\",\"name\":\"app.apk\",\"size\":3},{\"kind\":\"obb\",\"name\":\"main.obb\",\"size\":4}]}");
        File.WriteAllBytes(Path.Combine(game, "app.apk"), new byte[3]);
        File.WriteAllBytes(Path.Combine(game, "main.obb.part"), new byte[4]);
        var append = runtime.GetMethod("AppendInstalledGames", All);
        var arguments = new[] { collection, root, launcher, Path.Combine(root, "covers") };
        Check((int)append.Invoke(null, arguments) == 0, "Partial Venture installation is excluded");
        File.WriteAllBytes(Path.Combine(game, "main.obb"), new byte[3]);
        Check((int)append.Invoke(null, arguments) == 0, "Wrong-sized final file is excluded");
        File.WriteAllBytes(Path.Combine(game, "main.obb"), new byte[4]);
        Check((int)append.Invoke(null, arguments) == 1, "Completed Venture installation appears in Streamer collection");
        Check((int)append.Invoke(null, arguments) == 0, "Rescanning the same collection avoids duplicate tiles");

        string primary = Path.Combine(root, "Custom Games");
        string secondary = Path.Combine(root, "Other Games");
        string settings = Path.Combine(root, "settings.json");
        Func<string, string> jsonPath = path => path.Replace("\\", "\\\\");
        File.WriteAllText(settings, "{\"gamesDirectory\":\"" + jsonPath(primary) + "\"}");
        arguments[0] = Activator.CreateInstance(collectionType, true);
        Check((int)append.Invoke(null, arguments) == 1, "Venture finds default-folder games when a custom primary folder is configured");
        string movedGame = Path.Combine(primary, "com.example.alpha", "42");
        string extraGame = Path.Combine(secondary, "com.example.extra", "42");
        string defaultGame = Path.Combine(root, "games", "com.example.default", "42");
        Directory.CreateDirectory(movedGame);
        Directory.CreateDirectory(extraGame);
        Directory.CreateDirectory(defaultGame);
        foreach (string file in new[] { "plan.json", "app.apk", "main.obb" })
        {
            File.Copy(Path.Combine(game, file), Path.Combine(movedGame, file));
            File.Copy(Path.Combine(game, file), Path.Combine(extraGame, file));
            File.Copy(Path.Combine(game, file), Path.Combine(defaultGame, file));
        }
        string extraPlan = Path.Combine(extraGame, "plan.json");
        File.WriteAllText(extraPlan, File.ReadAllText(extraPlan).Replace("com.example.alpha", "com.example.extra"));
        string defaultPlan = Path.Combine(defaultGame, "plan.json");
        File.WriteAllText(defaultPlan, File.ReadAllText(defaultPlan).Replace("com.example.alpha", "com.example.default"));
        File.WriteAllText(settings, "{\"gamesDirectory\":\"" + jsonPath(primary) + "\",\"libraryFolders\":[\"" +
            jsonPath(secondary) + "\"]}");
        arguments[0] = Activator.CreateInstance(collectionType, true);
        Check((int)append.Invoke(null, arguments) == 3, "Venture scans primary, additional, and unlisted default folders without duplicate tiles");

        ulong appId = (ulong)runtime.GetMethod("EntryId", All).Invoke(null, new object[] { "com.example.alpha" });
        Type platform = assembly.GetType("VirtualDesktop.Interfaces.GamePlatform", true);
        Type id = collectionType.BaseType.GetGenericArguments()[0];
        object key = Activator.CreateInstance(id, new[] { Enum.Parse(platform, "OculusRift"), (object)appId });
        var lookup = collectionType.BaseType.GetMethods(All)
            .Single(method => method.ReturnType == typeof(bool) && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { id, itemType.MakeByRefType() }));
        object[] lookupArgs = { key, null };
        Check((bool)lookup.Invoke(collection, lookupArgs), "Headset category key is OculusRift");
        object tile = lookupArgs[1];
        Func<int, object> field = token => StreamerProfile.Method(module, token).Invoke(tile, null);
        Check((string)field(0x06000160) == "Alpha Test Game" &&
            (string)field(0x06000162) == launcher &&
            (string)field(0x06000164) == "--launch com.example.alpha" &&
            (string)field(0x06000166) == root &&
            File.Exists((string)field(0x06000168)), "Tile has display title, artwork, launcher, working directory and package arguments");
        Check(!(bool)field(0x0600016A) && !(bool)field(0x0600016C) && Convert.ToInt32(field(0x06000170)) == 0,
            "Venture entry leaves native Oculus launch and injection flags clear");

        var webp = runtime.Assembly.GetType("VirtualDesktop.LibraryOverrides.VentureWebp", true);
        using (var image = (IDisposable)webp.GetMethod("Read", All).Invoke(null, new object[] { args[3] }))
            Check(image != null, "Bundled decoder reads actual WebP artwork");
        CheckClose(module, runtime, key, root);

        preferencesType.GetMethod("SetEnabled").Invoke(preferences, new object[] { false });
        CheckCustomGames(module, runtime, collectionType, root);
        var stock = Activator.CreateInstance(collectionType, true);
        var stockTile = ((ConstructorInfo)StreamerProfile.Method(module, 0x06000172)).Invoke(new[] { Enum.Parse(platform, "OculusRift"), (object)12345UL });
        collectionType.GetMethod("Add", new[] { itemType }).Invoke(stock, new[] { stockTile });
        var fromResult = typeof(Task).GetMethod("FromResult").MakeGenericMethod(collectionType);
        runtime.GetField("original", All).SetValue(null, new Func<Task>(() => (Task)fromResult.Invoke(null, new[] { stock })));
        var refreshed = (Task)StreamerProfile.Method(module, 0x060001F4).Invoke(null, null);
        refreshed.GetAwaiter().GetResult();
        object result = refreshed.GetType().GetProperty("Result").GetValue(refreshed, null);
        object stockKey = Activator.CreateInstance(id, new[] { Enum.Parse(platform, "OculusRift"), (object)12345UL });
        Check(Object.ReferenceEquals(stock, result) && (bool)lookup.Invoke(result, new[] { stockKey, null }) &&
            !(bool)lookup.Invoke(result, new[] { key, null }), "Disabling Venture leaves the refreshed stock Oculus list intact");
        var tryLaunch = runtime.GetMethod("TryLaunch", All);
        Check((bool)tryLaunch.Invoke(null, new[] { key, (object)appId }) &&
            !(bool)tryLaunch.Invoke(null, new[] { stockKey, (object)12345UL }),
            "Disabled Venture intercepts stale tiles without intercepting ordinary Oculus games");

        var completion = Activator.CreateInstance(typeof(TaskCompletionSource<>).MakeGenericType(collectionType));
        var marker = new InvalidOperationException("Isolated original scan marker");
        completion.GetType().GetMethod("SetException", new[] { typeof(Exception) }).Invoke(completion, new object[] { marker });
        runtime.GetField("original", All).SetValue(null, new Func<Task>(() => (Task)completion.GetType().GetProperty("Task").GetValue(completion, null)));
        var pending = (Task)StreamerProfile.Method(module, 0x060001F4).Invoke(null, null);
        try { pending.GetAwaiter().GetResult(); }
        catch (InvalidOperationException error)
        {
            if (error != marker) throw;
            Console.WriteLine("PASS verified Streamer entry calls the alpha wrapper and preserves original scan errors");
            return;
        }
        throw new Exception("The installed game-list wrapper did not return the original scan error.");
    }

    private static void CheckClose(Module module, Type runtime, object key, string root)
    {
        string marker = Path.Combine(root, "child.pid");
        runtime.GetMethod("Register", All).Invoke(null, new[] {
            key, Assembly.GetExecutingAssembly().Location, "--job-parent \"" + marker + "\"" });
        var running = (IDictionary)runtime.GetField("runningGames", All).GetValue(null);
        Check(running[key] is Process, "Streamer sees the Venture supervisor as a running game");
        bool started = SpinWait.SpinUntil(() => File.Exists(marker) && new FileInfo(marker).Length > 0, 4000);
        Check(started, "The registered launcher started its worker process");
        using (var child = Process.GetProcessById(Int32.Parse(File.ReadAllText(marker))))
        {
            // Use the same process termination routine that Streamer's X handler uses.
            StreamerProfile.Method(module, 0x0602173B).Invoke(null, new[] { running[key] });
            Check(child.WaitForExit(4000), "The stock close routine stops the launcher and its worker");
            Check(SpinWait.SpinUntil(() => !running.Contains(key), 4000), "Streamer clears the Venture running state after exit");
        }
    }

    private static void CheckCustomGames(Module module, Type runtime, Type collectionType, string root)
    {
        string catalog = Path.Combine(root, "CustomGames.xml");
        string executable = Assembly.GetExecutingAssembly().Location;
        var document = new System.Xml.XmlDocument();
        document.LoadXml("<CustomGames version=\"1\"><Game id=\"3000000000\" name=\"Custom VR Game\" selected=\"true\" /></CustomGames>");
        var game = (System.Xml.XmlElement)document.DocumentElement.FirstChild;
        game.SetAttribute("executable", executable);
        game.SetAttribute("cover", Path.Combine(root, "covers", "custom.jpg"));
        document.Save(catalog);
        object collection = Activator.CreateInstance(collectionType, true);
        var append = runtime.GetMethod("AppendCustomGames", All);
        Check((int)append.Invoke(null, new[] { collection, catalog }) == 1,
            "Selected third-party executable appears with Venture disabled");
        Check((int)append.Invoke(null, new[] { collection, catalog }) == 0,
            "Repeated custom discovery avoids duplicate dashboard entries");
        Type id = collectionType.BaseType.GetGenericArguments()[0];
        object platform = Enum.Parse(module.Assembly.GetType("VirtualDesktop.Interfaces.GamePlatform", true), "OculusRift");
        ulong customId = 0x5644554300000000UL | 3000000000U;
        object key = Activator.CreateInstance(id, new[] { platform, (object)customId });
        Type item = collectionType.BaseType.GetGenericArguments()[1];
        var lookup = collectionType.BaseType.GetMethods(All).Single(method => method.ReturnType == typeof(bool) &&
            method.GetParameters().Select(parameter => parameter.ParameterType).SequenceEqual(new[] { id, item.MakeByRefType() }));
        object[] found = { key, null };
        Check((bool)lookup.Invoke(collection, found), "Custom game uses its own dashboard ID namespace");
        object entry = found[1];
        Check((string)((MethodInfo)StreamerProfile.Method(module, 0x06000160)).Invoke(entry, null) == "Custom VR Game" &&
            (string)((MethodInfo)StreamerProfile.Method(module, 0x06000162)).Invoke(entry, null) == executable &&
            (string)((MethodInfo)StreamerProfile.Method(module, 0x06000168)).Invoke(entry, null) == game.GetAttribute("cover"),
            "Custom tile preserves its title, executable and artwork path");
        string childMarker = Path.Combine(root, "custom-child.pid");
        Environment.SetEnvironmentVariable("VD_CUSTOM_TEST_CHILD_MARKER", childMarker);
        try
        {
            Check((bool)runtime.GetMethod("TryLaunch", All).Invoke(null, new[] { key, (object)customId }),
                "Custom game launch is handled independently of Venture");
        }
        finally { Environment.SetEnvironmentVariable("VD_CUSTOM_TEST_CHILD_MARKER", null); }
        var running = (IDictionary)runtime.GetField("runningGames", All).GetValue(null);
        Check(running[key] is Process && !((Process)running[key]).HasExited,
            "Custom launch registers a live process for the headset close button");
        Check(SpinWait.SpinUntil(() => File.Exists(childMarker) && new FileInfo(childMarker).Length > 0, 5000),
            "Custom bootstrapper starts a child game process");
        Thread.Sleep(500);
        using (var child = Process.GetProcessById(Int32.Parse(File.ReadAllText(childMarker))))
        {
            Check(running[key] is Process && !((Process)running[key]).HasExited && !child.HasExited,
                "Custom game stays registered after its bootstrapper exits");
            StreamerProfile.Method(module, 0x0602173B).Invoke(null, new[] { running[key] });
            Check(child.WaitForExit(4000), "The headset close routine stops the custom child game");
            Check(SpinWait.SpinUntil(() => !running.Contains(key), 4000), "Closing the custom game clears its running state");
        }
        game.SetAttribute("selected", "false");
        document.Save(catalog);
        Check((int)append.Invoke(null, new[] { Activator.CreateInstance(collectionType, true), catalog }) == 0,
            "Deselected custom programs are excluded from the headset library");
        game.SetAttribute("selected", "true");
        game.SetAttribute("executable", Path.Combine(root, "missing.exe"));
        document.Save(catalog);
        Check((int)append.Invoke(null, new[] { Activator.CreateInstance(collectionType, true), catalog }) == 0,
            "Missing third-party executables are excluded from the dashboard");
        File.Delete(catalog);
    }
}
