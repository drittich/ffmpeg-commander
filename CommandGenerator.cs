using System;
using System.Threading;
using System.Threading.Tasks;
using Azure.AI.OpenAI;
using Microsoft.Extensions.Configuration;
using OpenAI.Chat;
using System.ClientModel;

namespace em
{
    internal sealed class CommandGenerator
    {
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

        public async Task<string> GenerateFromDescription(string description, CancellationToken ct = default)
        {
            string fallback = "generated_command_for_" + description.Replace(" ", "_");

            string? endpoint = _config["AzureOpenAI:Endpoint"];
            string? apiKey = _config["AzureOpenAI:ApiKey"];
            string? deployment = _config["AzureOpenAI:Deployment"];

            // Fallback to the previous behavior if not configured.
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(deployment))
            {
                return fallback;
            }

            try
            {
                // Azure.AI.OpenAI v2.x uses AzureOpenAIClient + scenario clients (e.g., ChatClient).
                var azureClient = new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(apiKey));
                ChatClient chatClient = azureClient.GetChatClient(deployment);

                var messages = new ChatMessage[]
                {
                    new SystemChatMessage(GenerateSystemMessage),
                    new UserChatMessage(description)
                };

                var options = new ChatCompletionOptions
                {
                    // Keep responses short; enough for typical ffmpeg one-liners.
                    MaxOutputTokenCount = 256
                };

                ChatCompletion completion = await chatClient.CompleteChatAsync(messages, options, ct).ConfigureAwait(false);
                return ExtractSingleLineCommandText(completion, fallback: fallback);
            }
            catch
            {
                // Keep console logging out of this service for testability; fall back silently.
                return fallback;
            }
        }

        public async Task<string> AdjustFromInstruction(string previousFullCommand, string instruction, CancellationToken ct = default)
        {
            string? endpoint = _config["AzureOpenAI:Endpoint"];
            string? apiKey = _config["AzureOpenAI:ApiKey"];
            string? deployment = _config["AzureOpenAI:Deployment"];

            // If not configured, just return the previous command unchanged (minus leading "ffmpeg").
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(deployment))
            {
                return StripLeadingFfmpeg(previousFullCommand);
            }

            string currentArgs = StripLeadingFfmpeg(previousFullCommand);

            try
            {
                var azureClient = new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(apiKey));
                ChatClient chatClient = azureClient.GetChatClient(deployment);

                var messages = new ChatMessage[]
                {
                    new SystemChatMessage(AdjustSystemMessage),
                    new UserChatMessage(
                        "Current FFmpeg arguments:\n" + currentArgs + "\n\n" +
                        "Instruction:\n" + instruction)
                };

                var options = new ChatCompletionOptions
                {
                    MaxOutputTokenCount = 256
                };

                ChatCompletion completion = await chatClient.CompleteChatAsync(messages, options, ct).ConfigureAwait(false);
                return ExtractSingleLineCommandText(completion, fallback: currentArgs);
            }
            catch
            {
                // Keep prior behavior: if adjust fails, keep the current args.
                return currentArgs;
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

            if (completion.Content != null && completion.Content.Count > 0)
            {
                // Typically a single text part; take the first line just like before.
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