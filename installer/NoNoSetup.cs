using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace NoNoSetup
{
    internal static class Program
    {
        private const string PayloadResourceName = "NoNo.Setup.Payload.msi";

        [STAThread]
        private static int Main(string[] args)
        {
            string extractionPath = GetOption(args, "--extract-payload");
            if (!String.IsNullOrWhiteSpace(extractionPath))
            {
                try
                {
                    ExtractPayload(Path.GetFullPath(extractionPath));
                    return 0;
                }
                catch
                {
                    return 2;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (InstallProgressForm form = new InstallProgressForm(HasFlag(args, "--ui-self-test")))
            {
                Application.Run(form);
                return form.ExitCode;
            }
        }

        internal static void ExtractPayload(string destination)
        {
            string directory = Path.GetDirectoryName(destination);
            if (String.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("Invalid payload destination.");
            }
            Directory.CreateDirectory(directory);

            using (Stream input = Assembly.GetExecutingAssembly().GetManifestResourceStream(PayloadResourceName))
            {
                if (input == null)
                {
                    throw new InvalidOperationException("The embedded MSI payload is missing.");
                }
                using (FileStream output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                }
            }
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
    }

    internal sealed class InstallProgressForm : Form
    {
        private readonly Label titleLabel;
        private readonly Label detailLabel;
        private readonly ProgressBar progressBar;
        private readonly Button closeButton;
        private readonly bool uiSelfTest;

        public int ExitCode { get; private set; } = 1;

        public InstallProgressForm(bool uiSelfTest)
        {
            this.uiSelfTest = uiSelfTest;
            Text = "诺诺安装";
            ClientSize = new Size(440, 150);
            BackColor = Color.White;
            Font = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;
            MinimizeBox = false;
            ControlBox = false;
            ShowInTaskbar = true;
            AutoScaleMode = AutoScaleMode.Dpi;

            try
            {
                Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
            }

            titleLabel = new Label
            {
                AutoSize = false,
                Location = new Point(28, 24),
                Size = new Size(384, 30),
                Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold, GraphicsUnit.Point),
                Text = "正在安装诺诺"
            };
            Controls.Add(titleLabel);

            detailLabel = new Label
            {
                AutoSize = false,
                Location = new Point(28, 58),
                Size = new Size(384, 24),
                ForeColor = Color.FromArgb(80, 80, 80),
                Text = "正在更新程序和文字识别模型..."
            };
            Controls.Add(detailLabel);

            progressBar = new ProgressBar
            {
                Location = new Point(28, 94),
                Size = new Size(384, 14),
                Style = ProgressBarStyle.Marquee,
                MarqueeAnimationSpeed = 24
            };
            Controls.Add(progressBar);

            closeButton = new Button
            {
                Location = new Point(322, 112),
                Size = new Size(90, 30),
                Text = "关闭",
                Visible = false
            };
            closeButton.Click += delegate { Close(); };
            Controls.Add(closeButton);

            Shown += async delegate
            {
                if (this.uiSelfTest)
                {
                    await Task.Delay(250);
                    ExitCode = 0;
                    Close();
                    return;
                }
                await InstallAsync();
            };
        }

        private async Task InstallAsync()
        {
            InstallResult result = await Task.Run<InstallResult>(InstallPackage);
            if (result.Success)
            {
                ExitCode = 0;
                progressBar.Style = ProgressBarStyle.Continuous;
                progressBar.Value = 100;
                titleLabel.Text = "安装完成";
                detailLabel.Text = "正在启动诺诺...";
                TryLaunchApplication();
                await Task.Delay(700);
                Close();
                return;
            }

            ExitCode = result.ExitCode;
            progressBar.Style = ProgressBarStyle.Continuous;
            progressBar.Value = 0;
            titleLabel.Text = "安装失败";
            detailLabel.Text = "错误代码 " + result.ExitCode + "，日志：" + result.LogPath;
            closeButton.Visible = true;
            ControlBox = true;
        }

        private static InstallResult InstallPackage()
        {
            string extractionRoot = Path.Combine(
                Path.GetTempPath(),
                "NoNo-Setup-" + Guid.NewGuid().ToString("N"));
            string msiPath = Path.Combine(extractionRoot, "NoNo-Desktop-Pet.msi");
            string logPath = Path.Combine(
                Path.GetTempPath(),
                "NoNo-Desktop-Pet-Setup-" + DateTime.Now.ToString("yyyyMMddHHmmss") + ".log");

            try
            {
                Program.ExtractPayload(msiPath);
                ProcessStartInfo startInfo = new ProcessStartInfo
                {
                    FileName = "msiexec.exe",
                    Arguments = "/i " + Quote(msiPath) + " /qn /norestart /l*v " + Quote(logPath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (Process process = Process.Start(startInfo))
                {
                    if (process == null)
                    {
                        throw new InvalidOperationException("Could not start Windows Installer.");
                    }
                    process.WaitForExit();
                    int exitCode = process.ExitCode;
                    return new InstallResult
                    {
                        Success = exitCode == 0 || exitCode == 1641 || exitCode == 3010,
                        ExitCode = exitCode,
                        LogPath = logPath
                    };
                }
            }
            catch (Exception ex)
            {
                try
                {
                    File.AppendAllText(logPath, Environment.NewLine + ex, System.Text.Encoding.UTF8);
                }
                catch
                {
                }
                return new InstallResult { Success = false, ExitCode = 1, LogPath = logPath };
            }
            finally
            {
                TryDelete(msiPath);
                try
                {
                    if (Directory.Exists(extractionRoot))
                    {
                        Directory.Delete(extractionRoot, false);
                    }
                }
                catch
                {
                }
            }
        }

        private static string Quote(string value)
        {
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
            }
        }

        private static void TryLaunchApplication()
        {
            try
            {
                string executable = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "NoNo",
                    "NoNo-Standalone.exe");
                if (File.Exists(executable))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = executable,
                        WorkingDirectory = Path.GetDirectoryName(executable),
                        UseShellExecute = true
                    });
                }
            }
            catch
            {
            }
        }

        private sealed class InstallResult
        {
            public bool Success;
            public int ExitCode;
            public string LogPath;
        }
    }
}
