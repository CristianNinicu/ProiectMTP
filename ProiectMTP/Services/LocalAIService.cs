using System.Runtime.ExceptionServices;
using System.Security;
using LMKit.Model; // pentru LM și DeviceConfiguration
using LMKit.TextGeneration; // pentru MultiTurnConversation și TextGenerationResult
using LMKit.Licensing;
using LMKit;
using LM = LMKit.Model.LM;
using MultiTurnConversation = LMKit.TextGeneration.MultiTurnConversation;
using TextGenerationResult = LMKit.TextGeneration.TextGenerationResult;
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
        private readonly LM _model; // Încarcă modelul GGUF
        private readonly MultiTurnConversation _chat; // Sesiunea de chat (multi-turn)
        private readonly object _lock = new object();
        private bool _disposed = false;

        public LocalAIService(IConfiguration configuration)
        {
            try
            {
                // 0) Înregistrează cheia de licență LM-Kit (Community sau comercială)
                //    Înlocuiește cu cheia ta obținută de pe: https://lm-kit.com/products/community-edition/
                // LicenseManager.SetLicenseKey("YOUR_COMMUNITY_LICENSE_KEY");

                // 1) Obține calea către fișierul GGUF din appsettings.json
                var modelPath = configuration["Llama:ModelPath"];
                if (string.IsNullOrWhiteSpace(modelPath))
                    throw new ArgumentException("Calea către model nu este configurată în appsettings.json");

                if (!File.Exists(modelPath))
                    throw new FileNotFoundException($"Modelul GGUF nu a fost găsit la calea: {modelPath}");

                // 2) Configurare CPU-only: GpuLayerCount = 0
                //    Astfel LM-Kit va rula inferența doar pe CPU, fără Vulkan/CUDA.
                var deviceConfig = new LM.DeviceConfiguration
                {
                    GpuLayerCount = 0,
                };
                LMKit.Global.Runtime.LogLevel = LMKit.Global.Runtime.LMKitLogLevel.Information;
                LMKit.Global.Runtime.Initialize();

                // 3) Încarcă efectiv modelul în memorie și creează sesiunea de chat
                _model = new LM(modelPath, deviceConfig);
                var contextSize = 512;
                _chat = new MultiTurnConversation(_model, contextSize: _model.ContextLength);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Eroare la inițializarea LM-Kit: {ex.Message}", ex);
            }
        }

        [HandleProcessCorruptedStateExceptions]
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
                        var fullPrompt = new StringBuilder();
                        fullPrompt.AppendLine("You are a helpful assistant that outputs valid MySQL statements (no comments, no explanations).");
                        fullPrompt.Append("User instruction (Romanian): \"");
                        fullPrompt.Append(prompt);
                        fullPrompt.AppendLine("\"");

                        // 8) Trimitem prompt-ul în sesiunea de chat și primim un TextGenerationResult
                        var generationResult = _chat.Submit(fullPrompt.ToString());

                        // 9) Extragem textul generat (proprietatea GeneratedText)
                        var text = generationResult.Completion?.Trim() ?? string.Empty;

                        // 10) Dacă lipsește punctul și virgula la final, îl adăugăm
                        if (!string.IsNullOrEmpty(text) && !text.EndsWith(";"))
                            text += ";";
                        var final = CleanGeneratedText(text);
                        return final;
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Eroare la generarea textului: {ex.Message}", ex);
                    }
                }
            });
        }

        /// <summary>
        /// În cazul în care modelul adaugă text explicativ înainte de "CREATE TABLE",
        /// această metodă va găsi ULTIMA apariție a "CREATE TABLE" și va returna
        /// substring-ul de la acel punct până la primul semicolon.
        /// </summary>
        private string CleanGeneratedText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            // 1) Spargem pe linii și excludem liniile care par comentarii SQL
            //    (încep cu --, /*, *). Apoi le re-unim într-un singur șir:
            var lines = text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line =>
                {
                    var t = line.TrimStart();
                    return !t.StartsWith("--") && !t.StartsWith("/*") && !t.StartsWith("*");
                })
                .Select(line => line.Trim())
                .ToArray();

            // 2) Unim toate liniile într-un singur șir, eliminând spațiile de la capete
            var collapsed = string.Join(" ", lines).Trim();

            // 3) Căutăm **ultima** apariție a șirului "CREATE TABLE" (insensibil la majuscule)
            var keyword = "CREATE TABLE";
            var idx = collapsed.LastIndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                // 4) Extragem substring-ul începând de la "CREATE TABLE"
                var after = collapsed.Substring(idx);

                // 5) Găsim primul semicolon
                var semiIdx = after.IndexOf(';');
                if (semiIdx >= 0)
                {
                    // Returnăm substring-ul de la "CREATE TABLE" și până la semicolon inclus
                    return after.Substring(0, semiIdx + 1).Trim();
                }
                else
                {
                    // Dacă nu există ';', adăugăm unul la final
                    var candidate = after.Trim();
                    if (!candidate.EndsWith(";"))
                        candidate += ";";
                    return candidate;
                }
            }

            // 6) Dacă nu am găsit niciun "CREATE TABLE", trimitem tot textul (fallback),
            //    cu un semicolon la final (dacă lipsește).
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
            if (_disposed || !disposing) return;
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
                // Logare minimă a erorii la disposal
                Console.WriteLine($"Eroare la disposal: {ex.Message}");
            }
        }

        ~LocalAIService()
        {
            Dispose(false);
        }
    }
}