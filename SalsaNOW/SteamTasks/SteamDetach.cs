using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SalsaNOW
{
    internal class SteamDetach
    {
        public static void RemoveSteamEnvironments()
        {
            IDictionary variables =
                Environment.GetEnvironmentVariables(
                    EnvironmentVariableTarget.Process);

            var namesToRemove = new List<string>();

            foreach (DictionaryEntry variable in variables)
            {
                string name = variable.Key.ToString();

                if (IsSteamVariable(name))
                    namesToRemove.Add(name);
            }

            foreach (string name in namesToRemove)
            {
                Environment.SetEnvironmentVariable(
                    name,
                    null,
                    EnvironmentVariableTarget.Process);
            }
        }

        private static bool IsSteamVariable(string name)
        {
            string upper = name.ToUpperInvariant();

            return upper.Contains("STEAM") ||
                   upper.Contains("VALVE") ||
                   upper.StartsWith("FOSSILIZE_") ||
                   upper.StartsWith("MESA_") ||
                   upper.StartsWith("AMD_VK_") ||
                   upper.StartsWith("__GL_SHADER_") ||
                   upper.StartsWith("DXVK_STATE_CACHE") ||
                   upper.StartsWith("SDL_GAMECONTROLLER_");
        }
    }
}
