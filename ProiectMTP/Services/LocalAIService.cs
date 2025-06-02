using LMKit.Model;                // pentru LM și DeviceConfiguration
using LMKit.TextGeneration;       // pentru MultiTurnConversation și TextGenerationResult
using LMKit.Licensing;            // pentru LicenseManager
using System.Text;


namespace ProiectMTP.Services
{
    public interface IAIService
    {
        /// <summary>
        /// Primește un prompt în limbaj natural și returnează text generat de model.
        /// </summary>
        Task<string> GenerateAsync(string prompt);
    }

    public class LocalAIService : IAIService, IDisposable
    {
        private readonly LM                   _model;    // încarcă modelul GGUF
        private readonly MultiTurnConversation _chat;    // sesiunea de chat
        private readonly object                _lock = new object();
        private bool                           _disposed = false;

        public LocalAIService(IConfiguration configuration)
        {
            try
            {
                // 0) Înregistrează cheia de licență LM-Kit (community sau comercială)
                //    Înlocuiește cu cheia ta de pe https://lm-kit.com/products/community-edition/
                // LicenseManager.SetLicenseKey("YOUR_COMMUNITY_LICENSE_KEY");

                // 1) Obține calea către fișierul GGUF din appsettings.json
                var modelPath = configuration["Llama:ModelPath"];
                if (string.IsNullOrWhiteSpace(modelPath))
                    throw new ArgumentException("Calea către model nu este configurată în appsettings.json");

                if (!File.Exists(modelPath))
                    throw new FileNotFoundException($"Modelul GGUF nu a fost găsit la calea: {modelPath}");

                // 2) Configurează DeviceConfiguration cu GpuLayerCount = 0 → CPU-only
                var deviceConfig = new LM.DeviceConfiguration
                {
                    GpuLayerCount = 0   // forțează LM-Kit să ruleze tot pe CPU
                };

                // 3) Încarcă modelul în memorie și creează sesiunea de chat
                _model = new LM(modelPath, deviceConfig);
                _chat  = new MultiTurnConversation(_model);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Eroare la inițializarea LM-Kit: {ex.Message}", ex);
            }
        }

        public async Task<string> GenerateAsync(string prompt)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(LocalAIService));

            if (string.IsNullOrWhiteSpace(prompt))
                throw new ArgumentException("Prompt-ul nu poate fi gol", nameof(prompt));

            // MultiTurnConversation nu e thread-safe, deci protejăm apelul cu lock + Task.Run
            return await Task.Run(() =>
            {
                lock (_lock)
                {
                    if (_disposed)
                        throw new ObjectDisposedException(nameof(LocalAIService));

                    try
                    {
                        // 4) Construim prompt-ul foarte explicit pentru cod SQL
                        var fullPrompt = new StringBuilder();
                        fullPrompt.AppendLine("You are a helpful assistant. ONLY output the SQL statement, without any extra explanation or commentary.");
                        fullPrompt.AppendLine($"User instruction (Romanian): \"{prompt}\"");
                        fullPrompt.AppendLine();
                        fullPrompt.Append("Respond with a valid MariaDB CREATE TABLE statement, starting exactly with \"CREATE TABLE\" and ending with a semicolon. Do NOT include any leading or trailing text.");

                        // 5) Trimitem prompt-ul către sesiunea de chat și obținem rezultatul
                        TextGenerationResult result = _chat.Submit(fullPrompt.ToString());
                        var generated = result.Completion?.Trim() ?? string.Empty;


                        // 6) Extragem doar substring-ul care începe cu "CREATE TABLE" și se încheie cu ";"
                        var cleanSql = CleanGeneratedText(generated);
                        return cleanSql;
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Eroare la generarea textului: {ex.Message}", ex);
                    }
                }
            });
        }

        /// <summary>
        /// Elimină textul care apare înainte de "CREATE TABLE" și extrage numai instrucțiunea SQL completă
        /// (de la "CREATE TABLE" până la primul semicolon), curățând eventualele comentarii și spații multiple.
        /// </summary>
        private string CleanGeneratedText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            // 1) Spargem pe linii și excludem liniile de comentarii SQL
            var lines = text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line =>
                {
                    var t = line.TrimStart();
                    return !t.StartsWith("--") && !t.StartsWith("/*") && !t.StartsWith("*");
                })
                .Select(line => line.Trim())
                .ToArray();

            // 2) Reunim într-un singur șir și eliminăm spațiile la început/ sfârșit
            var collapsed = string.Join(" ", lines).Trim();

            // 3) Găsim indexul primei apariții a cuvântului "CREATE TABLE" (insensibil la majuscule)
            var keyword = "CREATE TABLE";
            var idx = collapsed.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                // 4) Extragem tot începând de la "CREATE TABLE"
                var after = collapsed.Substring(idx);

                // 5) Găsim poziția primului semicolon
                var semiIdx = after.IndexOf(';');
                if (semiIdx >= 0)
                {
                    // Returnăm substring-ul cu tot cu semicolon
                    return after.Substring(0, semiIdx + 1).Trim();
                }
                else
                {
                    // Dacă nu găsim ";", întoarcem tot textul rămas și adăugăm un semicolon
                    var candidate = after.Trim();
                    if (!candidate.EndsWith(";"))
                        candidate += ";";
                    return candidate;
                }
            }

            // 6) Dacă nu găsim "CREATE TABLE", trimitem tot șirul, cu un semicolon la final (fallback)
            var cleaned = collapsed;
            while (cleaned.Contains("  "))
                cleaned = cleaned.Replace("  ", " ");
            if (!cleaned.EndsWith(";"))
                cleaned += ";";
            return cleaned;
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                try
                {
                    lock (_lock)
                    {
                        _chat?.Dispose();
                        _model?.Dispose();
                        _disposed = true;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Eroare la disposal: {ex.Message}");
                }
            }
        }

        ~LocalAIService()
        {
            Dispose(false);
        }
    }
}
