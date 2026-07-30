using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

namespace NoNoInstaller
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool silent = HasFlag(args, "--silent");

            try
            {
                string codexRoot = GetOption(args, "--target");
                if (String.IsNullOrWhiteSpace(codexRoot))
                {
                    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    if (String.IsNullOrWhiteSpace(userProfile))
                    {
                        throw new InvalidOperationException("Could not locate the current user profile.");
                    }

                    codexRoot = Path.Combine(userProfile, ".codex");
                }

                InstallPackage(Path.GetFullPath(codexRoot));

                if (!silent)
                {
                    MessageBox.Show(
                        "诺诺 (NoNo) has been installed for Codex Desktop.\n\nRestart Codex Desktop or refresh the avatar/pet list in Settings.",
                        "诺诺 (NoNo) Installer",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }

                return 0;
            }
            catch (Exception ex)
            {
                if (!silent)
                {
                    MessageBox.Show(
                        "诺诺 (NoNo) installation failed:\n\n" + ex.Message,
                        "诺诺 (NoNo) Installer",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Error);
                }

                return 1;
            }
        }

        private static void InstallPackage(string codexRoot)
        {
            string avatarDir = Path.Combine(codexRoot, "avatars", "nono");
            string petDir = Path.Combine(codexRoot, "pets", "nono");

            Directory.CreateDirectory(avatarDir);
            Directory.CreateDirectory(petDir);

            WriteResource("avatar.json", Path.Combine(avatarDir, "avatar.json"));
            WriteResource("spritesheet.webp", Path.Combine(avatarDir, "spritesheet.webp"));
            WriteResource("pet.json", Path.Combine(petDir, "pet.json"));
            WriteResource("spritesheet.webp", Path.Combine(petDir, "spritesheet.webp"));
        }

        private static void WriteResource(string resourceName, string destination)
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using (Stream input = assembly.GetManifestResourceStream(resourceName))
            {
                if (input == null)
                {
                    throw new InvalidOperationException("Missing embedded resource: " + resourceName);
                }

                using (FileStream output = File.Create(destination))
                {
                    input.CopyTo(output);
                }
            }
        }

        private static bool HasFlag(string[] args, string name)
        {
            for (int i = 0; i < args.Length; i++)
            {
                if (String.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string GetOption(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (String.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                {
                    return args[i + 1];
                }
            }

            return null;
        }
    }
}
