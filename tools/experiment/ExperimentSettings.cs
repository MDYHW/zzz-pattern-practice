using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using System.Drawing;
using System.Collections.Generic;

namespace VesperLab
{
    public sealed class ExperimentSettings
    {
        public string Endpoint = "ws://127.0.0.1:4455", ProtectedPassword = "";
        public string Password()
        {
            return String.IsNullOrEmpty(ProtectedPassword) ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(ProtectedPassword), null, DataProtectionScope.CurrentUser));
        }
        public static ExperimentSettings Load(string root)
        {
            return Load(root, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "obs-studio", "plugin_config", "obs-websocket", "config.json"));
        }
        internal static ExperimentSettings Load(string root, string native)
        {
            string path = Path.Combine(root, "settings.json");
            if (File.Exists(path)) return new JavaScriptSerializer().Deserialize<ExperimentSettings>(File.ReadAllText(path));
            var settings = new ExperimentSettings();
            if (File.Exists(native))
            {
                var values = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(native));
                if (values.ContainsKey("server_port")) settings.Endpoint = "ws://127.0.0.1:" + Convert.ToInt32(values["server_port"]);
                if (values.ContainsKey("auth_required") && Convert.ToBoolean(values["auth_required"]) && values.ContainsKey("server_password"))
                    settings.SetPassword(Convert.ToString(values["server_password"]));
            }
            return settings;
        }
        private void SetPassword(string value)
        {
            ProtectedPassword = String.IsNullOrEmpty(value) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser));
        }
        public static bool Edit(IWin32Window owner, string root)
        {
            var settings = Load(root);
            using (var dialog = new Form { Text = "OBS 연결 설정", ClientSize = new Size(490, 180), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false })
            {
                var endpoint = new TextBox { Text = settings.Endpoint, Left = 115, Top = 20, Width = 345 };
                var password = new TextBox { Text = settings.Password(), UseSystemPasswordChar = true, Left = 115, Top = 60, Width = 345 };
                dialog.Controls.Add(new Label { Text = "WebSocket 주소", Left = 15, Top = 23, Width = 100 });
                dialog.Controls.Add(new Label { Text = "비밀번호", Left = 15, Top = 63, Width = 100 });
                dialog.Controls.Add(endpoint); dialog.Controls.Add(password);
                dialog.Controls.Add(new Label { Text = "OBS 도구 → WebSocket 서버 설정에서 서버를 켜세요.\n녹화 시작·종료는 OBS에서 직접 합니다.", Left = 15, Top = 99, Width = 450, Height = 40 });
                var save = new Button { Text = "저장 후 연결", Left = 335, Top = 142, Width = 125, DialogResult = DialogResult.OK };
                dialog.Controls.Add(save); dialog.AcceptButton = save;
                if (dialog.ShowDialog(owner) != DialogResult.OK) return false;
                Uri uri;
                if (!Uri.TryCreate(endpoint.Text.Trim(), UriKind.Absolute, out uri) || (uri.Scheme != "ws" && uri.Scheme != "wss") || uri.UserInfo.Length != 0)
                    throw new ArgumentException("ws:// 또는 wss:// 주소를 입력하세요.");
                settings.Endpoint = endpoint.Text.Trim(); settings.SetPassword(password.Text);
                Directory.CreateDirectory(root);
                File.WriteAllText(Path.Combine(root, "settings.json"), new JavaScriptSerializer().Serialize(settings));
                return true;
            }
        }
    }
}
