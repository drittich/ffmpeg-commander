using System;
using System.Diagnostics;
using System.Threading;
using Azure.AI.OpenAI;
using OpenAI.Chat;
using System.ClientModel;
using Microsoft.Extensions.Configuration;

namespace em
{
    class Program
    {
        private static string? s_lastAcceptedCommand;

        static void Main(string[] args)
        {
            if (args.Length > 0)
            {
                // Process initial command-line arguments as a command and exit.
                string initialInput = string.Join(" ", args).Trim();
                ProcessInput(initialInput);
                return;
            }
            
            // Start REPL mode.
            Console.WriteLine("em.exe REPL mode (type 'exit' to quit)");
            Console.WriteLine("Tip: after generating a command, type an adjustment like 'remove audio' to revise the last command.");
            while (true)
            {
                Console.Write("> ");
                string? input = Console.ReadLine()?.Trim();
                if (input == null || input.Equals("exit", StringComparison.OrdinalIgnoreCase))
                    break;

                if (input.Equals("clear", StringComparison.OrdinalIgnoreCase))
                {
                    s_lastAcceptedCommand = null;
                    Console.WriteLine("Cleared last accepted command.");
                    continue;
                }

                if (input.Length == 0)
                    continue;

                ProcessInput(input);
            }
        }
        
        static void ProcessInput(string input)
        {
            // Parse the input string into tokens
            string[] tokens = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string potentialProgram = "";
            string description = "";

            bool isFfmpegRequest = tokens.Length > 1 && string.Equals(tokens[0], "ffmpeg", StringComparison.OrdinalIgnoreCase);

            if (isFfmpegRequest)
            {
                potentialProgram = tokens[0];
                description = input.Substring(tokens[0].Length).Trim();
            }
            else
            {
                description = input;
            }

            // If the user didn't start with "ffmpeg", treat the input as an adjustment to the last accepted command (if present).
            bool isAdjustment = !isFfmpegRequest && !string.IsNullOrWhiteSpace(s_lastAcceptedCommand);

            string commandArgs = isAdjustment
                ? CallOpenAIAPIAdjust(s_lastAcceptedCommand!, description)
                : CallOpenAIAPI(description);

            // Default to ffmpeg if this is an adjustment (we only support adjusting ffmpeg commands right now).
            if (isAdjustment && string.IsNullOrEmpty(potentialProgram))
            {
                potentialProgram = "ffmpeg";
            }

            string commandResult = !string.IsNullOrEmpty(potentialProgram)
                ? potentialProgram + " " + commandArgs
                : commandArgs;

            // Output the generated command (do not enter edit mode automatically)
            Console.WriteLine("Generated command: " + commandResult);

            Console.WriteLine("Press Enter to accept, 'e' to edit, or any other key to cancel.");
            ConsoleKeyInfo acceptKey = Console.ReadKey(intercept: true);

            string acceptedCommand;

            if (acceptKey.Key == ConsoleKey.E)
            {
                Console.WriteLine();
                Console.Write("Edit command: ");
                acceptedCommand = EditInline(commandResult);
                Console.WriteLine();
            }
            else if (acceptKey.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                acceptedCommand = commandResult;
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("Command generation canceled.");
                return;
            }

            // Persist only after acceptance so future inputs can adjust it.
            s_lastAcceptedCommand = acceptedCommand;

            Console.WriteLine("Accepted command: " + acceptedCommand);

            // Wait for Enter key to execute the command
            Console.WriteLine("Press Enter to execute the command, or any other key to cancel.");
            ConsoleKeyInfo execKey = Console.ReadKey(intercept: true);
            if (execKey.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                Console.WriteLine("Executing command...");
                ExecuteCommand(acceptedCommand);
            }
            else
            {
                Console.WriteLine();
                Console.WriteLine("Command execution canceled.");
            }
        }
        
        static string CallOpenAIAPI(string description)
        {
            // appsettings.json:
            // {
            //   "AzureOpenAI": { "Endpoint": "...", "ApiKey": "...", "Deployment": "..." }
            // }
            IConfiguration config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .Build();

            string? endpoint = config["AzureOpenAI:Endpoint"];
            string? apiKey = config["AzureOpenAI:ApiKey"];
            string? deployment = config["AzureOpenAI:Deployment"];

            // Fallback to the previous behavior if not configured.
            if (string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(deployment))
            {
                return "generated_command_for_" + description.Replace(" ", "_");
            }

            try
            {
                // Azure.AI.OpenAI v2.x uses AzureOpenAIClient + scenario clients (e.g., ChatClient).
                var azureClient = new AzureOpenAIClient(new Uri(endpoint), new ApiKeyCredential(apiKey));
                ChatClient chatClient = azureClient.GetChatClient(deployment);

                var messages = new ChatMessage[]
                {
                    new SystemChatMessage(
                        "You write FFmpeg command lines. " +
                        "Return ONLY a single line of FFmpeg arguments (do not include the leading 'ffmpeg'). " +
                        "No explanations, no markdown, no backticks, no surrounding quotes. " +
                        "Prefer safe defaults. Use double-quotes around file paths that may contain spaces."),
                    new UserChatMessage(description)
                };

                var options = new ChatCompletionOptions
                {
                    // Keep responses short; enough for typical ffmpeg one-liners.
                    MaxOutputTokenCount = 256
                };

                ChatCompletion completion = chatClient.CompleteChat(messages, options, CancellationToken.None);
                return ExtractSingleLineCommandText(completion, fallback: "generated_command_for_" + description.Replace(" ", "_"));
            }
            catch (Exception ex)
            {
                Console.WriteLine("Azure OpenAI call failed: " + ex.Message);
                return "generated_command_for_" + description.Replace(" ", "_");
            }
        }

        static string CallOpenAIAPIAdjust(string previousFullCommand, string instruction)
        {
            // appsettings.json:
            // {
            //   "AzureOpenAI": { "Endpoint": "...", "ApiKey": "...", "Deployment": "..." }
            // }
            IConfiguration config = new ConfigurationBuilder()
                .SetBasePath(AppContext.BaseDirectory)
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
                .Build();

            string? endpoint = config["AzureOpenAI:Endpoint"];
            string? apiKey = config["AzureOpenAI:ApiKey"];
            string? deployment = config["AzureOpenAI:Deployment"];

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
                    new SystemChatMessage(
                        "You modify existing FFmpeg command lines. " +
                        "You will be given the current FFmpeg arguments and an instruction. " +
                        "Return ONLY the updated FFmpeg arguments (do not include the leading 'ffmpeg'). " +
                        "No explanations, no markdown, no backticks, no surrounding quotes. " +
                        "Preserve existing input/output paths unless instructed otherwise. " +
                        "If audio should be removed, add -an and remove audio-related options/filters."),
                    new UserChatMessage(
                        "Current FFmpeg arguments:\n" + currentArgs + "\n\n" +
                        "Instruction:\n" + instruction)
                };

                var options = new ChatCompletionOptions
                {
                    MaxOutputTokenCount = 256
                };

                ChatCompletion completion = chatClient.CompleteChat(messages, options, CancellationToken.None);
                return ExtractSingleLineCommandText(completion, fallback: currentArgs);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Azure OpenAI adjust call failed: " + ex.Message);
                return currentArgs;
            }
        }

        static string StripLeadingFfmpeg(string command)
        {
            string trimmed = command.TrimStart();
            return trimmed.StartsWith("ffmpeg ", StringComparison.OrdinalIgnoreCase)
                ? trimmed.Substring("ffmpeg ".Length).TrimStart()
                : trimmed;
        }

        static string ExtractSingleLineCommandText(ChatCompletion completion, string fallback)
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
        
        static string EditInline(string initial)
        {
            Console.Write(initial);

            string editedCommand = initial;
            int cursorPos = initial.Length;

            while (true)
            {
                ConsoleKeyInfo key = Console.ReadKey(intercept: true);

                if (key.Key == ConsoleKey.Enter)
                    break;

                if (key.Key == ConsoleKey.Backspace && cursorPos > 0)
                {
                    cursorPos--;
                    editedCommand = editedCommand.Remove(cursorPos, 1);
                    Console.Write("\b \b");
                    continue;
                }

                if (!char.IsControl(key.KeyChar))
                {
                    editedCommand = editedCommand.Insert(cursorPos, key.KeyChar.ToString());
                    cursorPos++;
                    Console.Write(key.KeyChar);
                }
            }

            return editedCommand;
        }

        static void ExecuteCommand(string command)
        {
            try
            {
                Process process = new Process();
                process.StartInfo.FileName = "cmd.exe";
                process.StartInfo.Arguments = "/C " + command;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                string error = process.StandardError.ReadToEnd();
                process.WaitForExit();
                Console.WriteLine("Command output:");
                Console.WriteLine(output);
                if (!string.IsNullOrEmpty(error))
                {
                    Console.WriteLine("Command error:");
                    Console.WriteLine(error);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error executing command: " + ex.Message);
            }
        }
    }
}