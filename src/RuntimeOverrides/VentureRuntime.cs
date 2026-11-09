using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using VirtualDesktop.Unlock;

namespace VirtualDesktop.LibraryOverrides
{
    public static class VentureRuntime
    {
        private static Func<Task> original;
        private static DynamicMethod adapter;
        private static Action<string> log;
        private static VenturePreferences preferences;
        private static readonly Regex Package = new Regex(@"\A[A-Za-z0-9_]+(?:\.[A-Za-z0-9_]+)+\z", RegexOptions.CultureInvariant);
        private static readonly object catalogLock = new object();
        private static readonly Dictionary<ulong, Tile> tiles = new Dictionary<ulong, Tile>();
        private static readonly Dictionary<object, VentureGameProcess> sessions = new Dictionary<object, VentureGameProcess>();
        private static IDictionary runningGames;
        private static MethodInfo chooseRuntime, notifyExit;

        // Called only after Manager has verified Streamer 1.34.25 and its exact hash.
        internal static void Install(Assembly assembly, Action<string> logger, VenturePreferences settings)
        {
            if (!settings.Enabled)
            {
                logger("Venture integration disabled in Settings.");
                return;
            }
            preferences = settings;
            Module module = assembly.ManifestModule;
            var entry = (MethodInfo)module.ResolveMethod(0x060001F4);
            Type collection = assembly.GetType("VirtualDesktop.Streamer.GameCollection", true);
            if (!entry.IsStatic || entry.GetParameters().Length != 0 || entry.ReturnType != typeof(Task<>).MakeGenericType(collection))
                throw new MissingMethodException("Venture game-list signature changed.");
            var close = module.ResolveMethod(0x060001E9);
            byte[] closeCode = close.GetMethodBody().GetILAsByteArray();
            if (closeCode[0] != 0x7E) throw new MissingFieldException("Venture running-game list changed.");
            runningGames = (IDictionary)module.ResolveField(BitConverter.ToInt32(closeCode, 1)).GetValue(null);
            chooseRuntime = (MethodInfo)module.ResolveMethod(0x060001FD);
            notifyExit = (MethodInfo)module.ResolveMethod(0x06000200);

            FieldInfo builder = module.ResolveField(0x04000171);
            FieldInfo state = module.ResolveField(0x04000170);
            Type stateMachine = builder.DeclaringType;
            Type builderType = typeof(AsyncTaskMethodBuilder<>).MakeGenericType(collection);
            if (!stateMachine.IsValueType || !typeof(IAsyncStateMachine).IsAssignableFrom(stateMachine) ||
                state.DeclaringType != stateMachine || state.FieldType != typeof(int) || builder.FieldType != builderType)
                throw new MissingFieldException("Venture game-list state machine changed.");

            // Preserve the original scan by recreating its compiler-generated async wrapper.
            var scan = new DynamicMethod("VDUnlockOriginalGameList", typeof(Task), Type.EmptyTypes, typeof(VentureRuntime), true);
            ILGenerator il = scan.GetILGenerator();
            var local = il.DeclareLocal(stateMachine);
            il.Emit(OpCodes.Ldloca, local);
            il.Emit(OpCodes.Initobj, stateMachine);
            il.Emit(OpCodes.Ldloca, local);
            il.Emit(OpCodes.Call, builderType.GetMethod("Create"));
            il.Emit(OpCodes.Stfld, builder);
            il.Emit(OpCodes.Ldloca, local);
            il.Emit(OpCodes.Ldc_I4_M1);
            il.Emit(OpCodes.Stfld, state);
            il.Emit(OpCodes.Ldloca, local);
            il.Emit(OpCodes.Ldflda, builder);
            il.Emit(OpCodes.Ldloca, local);
            il.Emit(OpCodes.Call, builderType.GetMethod("Start").MakeGenericMethod(stateMachine));
            il.Emit(OpCodes.Ldloca, local);
            il.Emit(OpCodes.Ldflda, builder);
            il.Emit(OpCodes.Call, builderType.GetProperty("Task").GetGetMethod());
            il.Emit(OpCodes.Ret);
            original = (Func<Task>)scan.CreateDelegate(typeof(Func<Task>));

            // GameCollection is internal to Streamer, so the typed adapter needs skipVisibility.
            adapter = new DynamicMethod("VDUnlockVentureGameList", entry.ReturnType, Type.EmptyTypes, typeof(VentureRuntime), true);
            il = adapter.GetILGenerator();
            il.Emit(OpCodes.Call, typeof(VentureRuntime).GetMethod("Load").MakeGenericMethod(collection));
            il.Emit(OpCodes.Ret);
            var descriptor = typeof(DynamicMethod).GetMethod("GetMethodDescriptor", BindingFlags.Instance | BindingFlags.NonPublic);
            var handle = (RuntimeMethodHandle)descriptor.Invoke(adapter, null);
            log = logger;
            LaunchPresetRuntime.Redirect(entry, handle, new byte[] { 0x55, 0x41, 0x56, 0x57, 0x56, 0x53, 0x48, 0x81, 0xEC, 0x80, 0, 0, 0 });
            log("Venture game-list hook installed.");
        }

        public static async Task<T> Load<T>()
        {
            T collection = await ((Task<T>)original()).ConfigureAwait(false);
            if (!preferences.Enabled) return collection;
            try
            {
                string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                string launcher = VenturePreferences.LauncherPath;
                string data = Path.Combine(local, "Venture");
                string art = Path.Combine(local, "Virtual Desktop Unlock", "VentureTiles");
                int count = AppendInstalledGames(collection, data, launcher, art);
                log("Venture added " + count + " completed games to Oculus Games.");
            }
            catch (Exception error) { log("Venture game list skipped: " + error.Message); }
            return collection;
        }

        internal static int AppendInstalledGames(object collection, string data, string launcher, string art)
        {
            string games = Path.Combine(data, "games");
            if (!File.Exists(launcher) || !Directory.Exists(games)) return 0;
            Type collectionType = collection.GetType();
            Type item = collectionType.BaseType.GetGenericArguments()[1];
            Module module = item.Module;
            var constructor = (ConstructorInfo)module.ResolveMethod(0x06000172);
            MethodInfo add = collectionType.GetMethod("Add", new[] { item });
            Type idType = collectionType.BaseType.GetGenericArguments()[0];
            MethodInfo lookup = collectionType.BaseType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .Single(method => method.ReturnType == typeof(bool) && method.GetParameters().Select(p => p.ParameterType).SequenceEqual(new[] { idType, item.MakeByRefType() }));
            var setters = new[] { 0x06000161, 0x06000163, 0x06000165, 0x06000167, 0x06000169 }
                .Select(token => (MethodInfo)module.ResolveMethod(token)).ToArray();
            if (constructor.DeclaringType != item || add == null ||
                setters.Any(setter => setter.DeclaringType != item || setter.GetParameters().Single().ParameterType != typeof(string)))
                throw new MissingMemberException("Venture game entry layout changed.");
            Type platform = constructor.GetParameters()[0].ParameterType;
            object oculus = Enum.Parse(platform, "OculusRift");
            ConstructorInfo idConstructor = idType.GetConstructor(new[] { platform, typeof(ulong) });

            var names = ReadTitles(Path.Combine(data, "library.json"));
            int count = 0;
            var packages = new HashSet<string>(StringComparer.Ordinal);
            lock (catalogLock)
            {
                foreach (string directory in Directory.EnumerateDirectories(games))
                {
                    string package = Path.GetFileName(directory);
                    if (package.Length > 255 || !Package.IsMatch(package)) continue;
                    foreach (string version in Directory.EnumerateDirectories(directory))
                    {
                        string appId;
                        if (!IsComplete(version, package, out appId)) continue;
                        if (!packages.Add(package)) break;
                        ulong id = EntryId(package);
                        object key = idConstructor.Invoke(new object[] { oculus, id });
                        if ((bool)lookup.Invoke(collection, new[] { key, null })) break;
                        string title;
                        if (!names.TryGetValue(appId, out title)) title = package;
                        string cover = Cover(art, data, id, appId, title);
                        object entry = constructor.Invoke(new object[] { oculus, id });
                        string[] values = { title, launcher, "--launch " + package, Path.GetDirectoryName(launcher), cover };
                        for (int index = 0; index < setters.Length; index++)
                            setters[index].Invoke(entry, new object[] { values[index] });
                        add.Invoke(collection, new[] { entry });
                        tiles[id] = new Tile { Item = entry, Launcher = launcher, Arguments = values[2] };
                        count++;
                        break;
                    }
                }
            }
            return count;
        }

        private static ulong EntryId(string package)
        {
            using (var sha = SHA256.Create())
                // The headset caches covers by ID and otherwise keeps the first alpha's title cards.
                return BitConverter.ToUInt64(sha.ComputeHash(Encoding.UTF8.GetBytes("VDUnlock.Venture:art2:" + package)), 0) | 0x8000000000000000UL;
        }

        internal static bool TryLaunch(object game, ulong id)
        {
            lock (catalogLock)
            {
                Tile tile;
                if (!tiles.TryGetValue(id, out tile)) return false;
                if (sessions.ContainsKey(game)) return true;
                if (!preferences.Enabled)
                {
                    ReportExit(game);
                    return true;
                }
                try
                {
                    chooseRuntime.Invoke(null, new[] { tile.Item, null });
                    Register(game, tile.Launcher, tile.Arguments);
                }
                catch (Exception error)
                {
                    log("Venture launch failed: " + error.GetBaseException().Message);
                    ReportExit(game);
                }
                return true;
            }
        }

        private static void Register(object game, string launcher, string arguments)
        {
            VentureGameProcess session = VentureGameProcess.Start(launcher, arguments);
            try
            {
                runningGames.Add(game, session.Process);
                sessions.Add(game, session);
                int processId = session.Process.Id;
                session.Process.Exited += delegate
                {
                    lock (catalogLock)
                    {
                        if (Object.ReferenceEquals(runningGames[game], session.Process)) runningGames.Remove(game);
                        sessions.Remove(game);
                        session.Dispose();
                        ReportExit(game);
                        log("Venture supervisor exited: " + processId);
                    }
                };
                session.Process.EnableRaisingEvents = true;
                log("Venture supervisor registered with Streamer: " + processId);
            }
            catch
            {
                if (Object.ReferenceEquals(runningGames[game], session.Process)) runningGames.Remove(game);
                sessions.Remove(game);
                session.Dispose();
                throw;
            }
        }

        private static void ReportExit(object game)
        {
            try { notifyExit.Invoke(null, new[] { game }); }
            catch (Exception error) { log("Venture exit notification failed: " + error.GetBaseException().Message); }
        }

        private sealed class Tile
        {
            internal object Item;
            internal string Launcher, Arguments;
        }

        private static bool IsComplete(string version, string package, out string id)
        {
            id = "";
            var plan = ReadObject(Path.Combine(version, "plan.json"));
            if (Value(plan, "package") != package) return false;
            bool hasApk = false;
            foreach (var file in Records(plan, "files"))
            {
                string kind = Value(file, "kind");
                if (kind != "apk" && kind != "obb") continue;
                string name = Value(file, "name");
                long expected;
                if (String.IsNullOrEmpty(name) || Path.GetFileName(name) != name || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                    !Int64.TryParse(Value(file, "size"), out expected) || expected < 0) return false;
                var info = new FileInfo(Path.Combine(version, name));
                if (!info.Exists || info.Length == 0 || (expected != 0 && info.Length != expected)) return false;
                hasApk |= kind == "apk";
            }
            id = Value(plan, "appId");
            return hasApk;
        }

        private static Dictionary<string, string> ReadTitles(string path)
        {
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var game in Records(ReadObject(path), "games"))
            {
                string id = Value(game, "id");
                string name = Value(game, "name");
                if (id.Length != 0 && !String.IsNullOrWhiteSpace(name)) names[id] = name;
            }
            return names;
        }

        private static Dictionary<string, object> ReadObject(string path)
        {
            try
            {
                var file = new FileInfo(path);
                if (!file.Exists || file.Length > 4 * 1024 * 1024) return null;
                return new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024 }.Deserialize<Dictionary<string, object>>(File.ReadAllText(path));
            }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            catch (IOException) { return null; }
        }

        private static IEnumerable<Dictionary<string, object>> Records(Dictionary<string, object> map, string key)
        {
            object value;
            if (map == null || !map.TryGetValue(key, out value)) return Enumerable.Empty<Dictionary<string, object>>();
            var records = value as System.Collections.IEnumerable;
            return records == null ? Enumerable.Empty<Dictionary<string, object>>() : records.OfType<Dictionary<string, object>>();
        }

        private static string Value(Dictionary<string, object> map, string key)
        {
            object value;
            return map != null && map.TryGetValue(key, out value) ? Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) : "";
        }

        private static string Cover(string output, string data, ulong id, string appId, string title)
        {
            string path = Path.Combine(output, id + ".jpg");
            if (File.Exists(path)) return path;
            Directory.CreateDirectory(output);
            using (var bitmap = new Bitmap(616, 353))
            using (var graphics = Graphics.FromImage(bitmap))
            using (var font = new Font(FontFamily.GenericSansSerif, 34, FontStyle.Bold))
            using (var label = new Font(FontFamily.GenericSansSerif, 18))
            {
                graphics.Clear(Color.FromArgb(22, 27, 46));
                if (appId.Length != 0 && appId.All(c => c >= '0' && c <= '9'))
                {
                    string cached = Path.Combine(data, "cache", "art", appId + ".image");
                    if (DrawCachedCover(graphics, cached, bitmap.Width, bitmap.Height))
                    {
                        bitmap.Save(path, ImageFormat.Jpeg);
                        return path;
                    }
                }
                graphics.DrawString(title, font, Brushes.White, new RectangleF(35, 78, 546, 180));
                graphics.DrawString("Venture", label, Brushes.Aquamarine, 35, 280);
                bitmap.Save(path, ImageFormat.Jpeg);
                return path;
            }
        }

        private static bool DrawCachedCover(Graphics graphics, string path, int width, int height)
        {
            if (!File.Exists(path)) return false;
            try
            {
                using (var image = VentureWebp.Read(path) ?? Image.FromFile(path))
                    graphics.DrawImage(image, 0, 0, width, height);
                return true;
            }
            catch (ArgumentException) { return false; }
            // GDI+ reports unsupported image encodings as OutOfMemoryException.
            catch (OutOfMemoryException) { return false; }
            catch (System.ComponentModel.Win32Exception) { return false; }
            catch (IOException) { return false; }
        }
    }
}
