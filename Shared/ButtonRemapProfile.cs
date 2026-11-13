using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace VirtualDesktop.Unlock
{
    public enum RemapButton
    {
        A = 1, B = 2, X = 4, Y = 8,
        RThumb = 0x10, RTrigger = 0x20, RGrip = 0x40,
        LThumb = 0x80, LTrigger = 0x100, LGrip = 0x200
    }

    public sealed class ButtonSwap
    {
        public readonly RemapButton First;
        public readonly RemapButton Second;

        public ButtonSwap(RemapButton first, RemapButton second)
        {
            First = first;
            Second = second;
        }
    }

    public sealed class ButtonRemapProfile
    {
        public const int MaxSwaps = 5;
        public static string DefaultDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Virtual Desktop Unlock", "ButtonRemaps"); }
        }

        public readonly IList<ButtonSwap> Swaps;

        public ButtonRemapProfile(IEnumerable<ButtonSwap> swaps)
        {
            var list = new List<ButtonSwap>(swaps);
            int used = 0;
            foreach (var swap in list)
            {
                int mask = (int)swap.First | (int)swap.Second;
                if (!Enum.IsDefined(typeof(RemapButton), swap.First) || !Enum.IsDefined(typeof(RemapButton), swap.Second) ||
                    swap.First == swap.Second || (used & mask) != 0)
                    throw new InvalidDataException("Choose two different buttons per swap and use each button only once.");
                used |= mask;
            }
            Swaps = list.AsReadOnly();
        }

        public void Apply(ref short buttons, ref short touches,
            ref byte leftTrigger, ref byte rightTrigger, ref byte leftGrip, ref byte rightGrip)
        {
            for (int i = 0; i < Swaps.Count; i++)
            {
                var swap = Swaps[i];
                int first = (int)swap.First;
                int second = (int)swap.Second;
                bool firstPressed = (buttons & first) != 0;
                bool secondPressed = (buttons & second) != 0;
                int firstTouch = TouchMask(swap.First);
                int secondTouch = TouchMask(swap.Second);
                bool firstTouched = firstTouch == 0 ? firstPressed : (touches & firstTouch) != 0;
                bool secondTouched = secondTouch == 0 ? secondPressed : (touches & secondTouch) != 0;
                byte firstPressure = Pressure(swap.First, firstPressed, leftTrigger, rightTrigger, leftGrip, rightGrip);
                byte secondPressure = Pressure(swap.Second, secondPressed, leftTrigger, rightTrigger, leftGrip, rightGrip);

                buttons = SetFlag(SetFlag(buttons, first, secondPressed), second, firstPressed);
                touches = SetFlag(SetFlag(touches, firstTouch, secondTouched), secondTouch, firstTouched);
                SetPressure(swap.First, secondPressure, ref leftTrigger, ref rightTrigger, ref leftGrip, ref rightGrip);
                SetPressure(swap.Second, firstPressure, ref leftTrigger, ref rightTrigger, ref leftGrip, ref rightGrip);
            }
        }

        internal static int TouchMask(RemapButton button)
        {
            // The grip bit positions in the touch mask belong to thumb rests.
            return button == RemapButton.LGrip || button == RemapButton.RGrip ? 0 : (int)button;
        }

        private static short SetFlag(short flags, int mask, bool enabled)
        {
            int bits = unchecked((ushort)flags);
            return unchecked((short)(enabled ? bits | mask : bits & ~mask));
        }

        private static byte Pressure(RemapButton button, bool pressed, byte leftTrigger, byte rightTrigger, byte leftGrip, byte rightGrip)
        {
            switch (button)
            {
                case RemapButton.LTrigger: return leftTrigger;
                case RemapButton.RTrigger: return rightTrigger;
                case RemapButton.LGrip: return leftGrip;
                case RemapButton.RGrip: return rightGrip;
                default: return pressed ? Byte.MaxValue : (byte)0;
            }
        }

        private static void SetPressure(RemapButton button, byte pressure,
            ref byte leftTrigger, ref byte rightTrigger, ref byte leftGrip, ref byte rightGrip)
        {
            switch (button)
            {
                case RemapButton.LTrigger: leftTrigger = pressure; break;
                case RemapButton.RTrigger: rightTrigger = pressure; break;
                case RemapButton.LGrip: leftGrip = pressure; break;
                case RemapButton.RGrip: rightGrip = pressure; break;
            }
        }

        private static string GamePath(string directory, uint id)
        {
            if (id == 0) throw new ArgumentOutOfRangeException("id");
            return Path.Combine(directory, id.ToString(CultureInfo.InvariantCulture) + ".txt");
        }

        public static ButtonRemapProfile Read(string directory, uint id)
        {
            var swaps = new List<ButtonSwap>();
            string path = GamePath(directory, id);
            if (!File.Exists(path)) return new ButtonRemapProfile(swaps);
            using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var reader = new StreamReader(file))
            {
                if (file.Length > 128) throw new InvalidDataException("Invalid button remap profile.");
                while (true)
                {
                    var line = reader.ReadLine();
                    if (line == null) break;
                    var buttons = line.Split(' ');
                    RemapButton first, second;
                    if (buttons.Length != 2 || !Enum.TryParse(buttons[0], out first) || !Enum.TryParse(buttons[1], out second))
                        throw new InvalidDataException("Invalid button remap profile.");
                    swaps.Add(new ButtonSwap(first, second));
                }
            }
            return new ButtonRemapProfile(swaps);
        }

        public void Save(string directory, uint id)
        {
            string path = GamePath(directory, id);
            if (Swaps.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            Directory.CreateDirectory(directory);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var writer = new StreamWriter(temporary))
                    foreach (var swap in Swaps) writer.WriteLine(swap.First + " " + swap.Second);
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { File.Delete(temporary); }
        }
    }
}
