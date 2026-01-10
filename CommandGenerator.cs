using System;
using System.Threading;
using System.Threading.Tasks;
using Azure;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;

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

        public virtual async Task<GeneratorCallResult> GenerateFromDescription(string description, CancellationToken ct = default)
        {
            string fallback = "generated_command_for_" + (description ?? string.Empty).Replace(" ", "_");

            string? endpoint = _config["AzureOpenAI:Endpoint"];
            string? apiKey = _config["AzureOpenAI:ApiKey"];
            string? deployment = _config["AzureOpenAI:Deployment"];

            // Preserve prior behavior: if not configured, return a deterministic fallback.
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(deployment))
            {
                return new GeneratorCallResult(fallback, Error: null);
            }

            try
            {
                var azureClient = new OpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));

                var options = new ChatCompletionsOptions
                {
                    DeploymentName = deployment
                };
                options.Messages.Add(new ChatRequestSystemMessage(GenerateSystemMessage));
                options.Messages.Add(new ChatRequestUserMessage(description));

                Response<ChatCompletions> response = await azureClient.GetChatCompletionsAsync(options, ct).ConfigureAwait(false);
                ChatCompletions completion = response.Value;
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

            // If not configured, just return the previous command unchanged (minus leading "ffmpeg").
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(deployment))
            {
                return new GeneratorCallResult(StripLeadingFfmpeg(previousFullCommand), Error: null);
            }

            string currentArgs = StripLeadingFfmpeg(previousFullCommand);

            try
            {
                var azureClient = new OpenAIClient(new Uri(endpoint), new AzureKeyCredential(apiKey));

                var options = new ChatCompletionsOptions
                {
                    DeploymentName = deployment
                };
                options.Messages.Add(new ChatRequestSystemMessage(AdjustSystemMessage));
                options.Messages.Add(new ChatRequestUserMessage(
                    "Current FFmpeg arguments:\n" + currentArgs + "\n\n" +
                    "Instruction:\n" + instruction));

                Response<ChatCompletions> response = await azureClient.GetChatCompletionsAsync(options, ct).ConfigureAwait(false);
                ChatCompletions completion = response.Value;
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

        private static string ExtractSingleLineCommandText(ChatCompletions completion, string fallback)
        {
            string content = string.Empty;

            if (completion?.Choices != null && completion.Choices.Count > 0)
            {
                content = completion.Choices[0].Message?.Content ?? string.Empty;
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
