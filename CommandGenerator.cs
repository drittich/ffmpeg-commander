using System;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using OpenAI.Chat;

namespace em
{
    internal class CommandGenerator
    {
        internal readonly record struct GeneratorCallResult(string Value, string? Error);

        private readonly IConfiguration _config;

        // appsettings.json:
        // {
        //   "AzureOpenAI": { "Endpoint": "...", "ApiKey": "...", "Deployment": "..." }
        // }
        public CommandGenerator(IConfiguration? config = null)
        {
            _config = config ?? new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .Build();
        }

        /// <summary>
        /// True only when AzureOpenAI:Endpoint, AzureOpenAI:ApiKey, and AzureOpenAI:Deployment are all
        /// present (non-empty) in configuration. The UI can use this to detect the "not configured" state up front.
        /// </summary>
        public bool IsConfigured =>
            !string.IsNullOrWhiteSpace(_config["AzureOpenAI:Endpoint"]) &&
            !string.IsNullOrWhiteSpace(_config["AzureOpenAI:ApiKey"]) &&
            !string.IsNullOrWhiteSpace(_config["AzureOpenAI:Deployment"]);

        internal const string NotConfiguredMessage =
            "AI is not configured. Set AzureOpenAI:Endpoint, AzureOpenAI:ApiKey, and AzureOpenAI:Deployment in appsettings.json.";

        public virtual async Task<GeneratorCallResult> GenerateFromDescription(string description, CancellationToken ct = default)
        {
            string? endpoint = _config["AzureOpenAI:Endpoint"];
            string? apiKey = _config["AzureOpenAI:ApiKey"];
            string? deployment = _config["AzureOpenAI:Deployment"];

            // Not configured: do NOT fabricate a fake command. Surface a clear error instead.
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(deployment))
            {
                return new GeneratorCallResult(string.Empty, NotConfiguredMessage);
            }

            try
            {
                var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
                ChatClient chatClient = azureClient.GetChatClient(deployment);

                ChatCompletion completion = await chatClient.CompleteChatAsync(
                    new ChatMessage[]
                    {
                        new SystemChatMessage(GenerateSystemMessage),
                        new UserChatMessage(description),
                    },
                    cancellationToken: ct).ConfigureAwait(false);

                string args = ExtractSingleLineCommandText(completion, fallback: string.Empty);


                if (string.IsNullOrWhiteSpace(args))
                {
                    return new GeneratorCallResult(string.Empty, "OpenAI generation returned empty output.");
                }

                return new GeneratorCallResult(args, Error: null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                string context =
                    $"endpointHost={SafeHost(endpoint)} deployment={deployment} " +
                    "options.MaxTokens=(unset)";

                return new GeneratorCallResult(string.Empty, BuildOpenAiFailure("generation", ex, context));
            }
        }

        public virtual async Task<GeneratorCallResult> AdjustFromInstruction(string previousFullCommand, string instruction, CancellationToken ct = default)
        {
            string? endpoint = _config["AzureOpenAI:Endpoint"];
            string? apiKey = _config["AzureOpenAI:ApiKey"];
            string? deployment = _config["AzureOpenAI:Deployment"];

            // Not configured: surface the same error, but keep the previous args as Value so nothing is lost.
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(deployment))
            {
                return new GeneratorCallResult(StripLeadingFfmpeg(previousFullCommand), NotConfiguredMessage);
            }

            string currentArgs = StripLeadingFfmpeg(previousFullCommand);

            try
            {
                var azureClient = new AzureOpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));
                ChatClient chatClient = azureClient.GetChatClient(deployment);

                ChatCompletion completion = await chatClient.CompleteChatAsync(
                    new ChatMessage[]
                    {
                        new SystemChatMessage(AdjustSystemMessage),
                        new UserChatMessage(
                            "Current FFmpeg arguments:\n" + currentArgs + "\n\n" +
                            "Instruction:\n" + instruction),
                    },
                    cancellationToken: ct).ConfigureAwait(false);

                string updated = ExtractSingleLineCommandText(completion, fallback: currentArgs);

                // If the model produced nothing usable, keep prior args (but surface an error upstream).
                if (string.IsNullOrWhiteSpace(updated))
                {
                    return new GeneratorCallResult(currentArgs, "OpenAI adjust returned empty output (kept previous command).");
                }

                return new GeneratorCallResult(updated, Error: null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Keep prior behavior: if adjust fails, keep the current args, but surface the failure upstream.
                string context =
                    $"endpointHost={SafeHost(endpoint)} deployment={deployment} " +
                    "options.MaxTokens=(unset)";

                return new GeneratorCallResult(currentArgs, BuildOpenAiFailure("adjust", ex, context) + " (kept previous command).");
            }
        }

        private const string GenerateSystemMessage =
            "You write FFmpeg command lines. " +
            "Return ONLY a single line of FFmpeg arguments (do not include the leading 'ffmpeg'). " +
            "No explanations, no markdown, no backticks, no surrounding quotes. " +
            "Prefer safe defaults. Use double-quotes around file paths that may contain spaces.";

        private const string AdjustSystemMessage =
            "You modify existing FFmpeg command lines. " +
            "You will be given the current FFmpeg arguments and an instruction. " +
            "Return ONLY the updated FFmpeg arguments (do not include the leading 'ffmpeg'). " +
            "No explanations, no markdown, no backticks, no surrounding quotes. " +
            "Preserve existing input/output paths unless instructed otherwise. " +
            "If audio should be removed, add -an and remove audio-related options/filters.";

        private static string BuildOpenAiFailure(string action, Exception ex, string context)
        {
            // Keep this concise but more informative than ex.Message alone.
            string type = ex.GetType().FullName ?? ex.GetType().Name;

            string inner = ex.InnerException == null
                ? string.Empty
                : $" | Inner={ex.InnerException.GetType().FullName}: {ex.InnerException.Message}";

            return $"OpenAI {action} failed: {ex.Message} ({type}) | {context}{inner}";
        }

        private static string SafeHost(string endpoint)
        {
            try
            {
                return new Uri(endpoint).Host;
            }
            catch
            {
                return "(invalid-endpoint)";
            }
        }

        private static string StripLeadingFfmpeg(string command)
        {
            string trimmed = command.TrimStart();
            return trimmed.StartsWith("ffmpeg ", StringComparison.OrdinalIgnoreCase)
                ? trimmed.Substring("ffmpeg ".Length).TrimStart()
                : trimmed;
        }

        private static string ExtractSingleLineCommandText(ChatCompletion completion, string fallback)
        {
            string content = string.Empty;

            if (completion?.Content != null && completion.Content.Count > 0)
            {
                content = completion.Content[0].Text ?? string.Empty;
            }

            content = content.Replace("\r", "");
            int nl = content.IndexOf('\n');
            if (nl >= 0)
                content = content.Substring(0, nl);

            content = content.Trim();

            if (content.StartsWith("```", StringComparison.Ordinal))
            {
                content = content.Trim('`').Trim();
            }

            // If the model accidentally included the program name, strip it.
            if (content.StartsWith("ffmpeg ", StringComparison.OrdinalIgnoreCase))
            {
                content = content.Substring("ffmpeg ".Length).TrimStart();
            }

            return content.Length == 0 ? fallback : content;
        }
    }
}
