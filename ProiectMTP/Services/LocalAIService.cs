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
        Task<string> GenerateAsync(string prompt);
    }

    public class LocalAIService : IAIService, IDisposable
    {
        private readonly LM _model;
        private readonly MultiTurnConversation _chat;
        private readonly object _lock = new object();
        private bool _disposed = false;

        public LocalAIService(IConfiguration configuration)
        {
            try
            {
                var modelPath = configuration["Llama:ModelPath"];
                if (string.IsNullOrWhiteSpace(modelPath))
                    throw new ArgumentException("Calea către model nu este configurată în appsettings.json");

                if (!File.Exists(modelPath))
                    throw new FileNotFoundException($"Modelul GGUF nu a fost găsit la calea: {modelPath}");
                
                var deviceConfig = new LM.DeviceConfiguration
                {
                    GpuLayerCount = 0,
                };
                LMKit.Global.Runtime.LogLevel = LMKit.Global.Runtime.LMKitLogLevel.Information;
                LMKit.Global.Runtime.Initialize();
                
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

                        //Trimitem prompt-ul în sesiunea de chat și primim un TextGenerationResult
                        var generationResult = _chat.Submit(fullPrompt.ToString());

                        //Extragem textul generat
                        var text = generationResult.Completion?.Trim() ?? string.Empty;

                        //Dacă lipseste punctul si virgula la final, il adaugam
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
        
        private string CleanGeneratedText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;
            
            var lines = text
                .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Where(line =>
                {
                    var t = line.TrimStart();
                    return !t.StartsWith("--") && !t.StartsWith("/*") && !t.StartsWith("*");
                })
                .Select(line => line.Trim())
                .ToArray();
            
            var collapsed = string.Join(" ", lines).Trim();

            var keyword = "CREATE TABLE";
            var idx = collapsed.LastIndexOf(keyword, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var after = collapsed.Substring(idx);

                var semiIdx = after.IndexOf(';');
                if (semiIdx >= 0)
                {
                    return after.Substring(0, semiIdx + 1).Trim();
                }
                else
                {
                    var candidate = after.Trim();
                    if (!candidate.EndsWith(";"))
                        candidate += ";";
                    return candidate;
                }
            }
            
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
                Console.WriteLine($"Eroare la disposal: {ex.Message}");
            }
        }
        ~LocalAIService()
        {
            Dispose(false);
        }
    }
}