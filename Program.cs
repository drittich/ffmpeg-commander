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
            while (true)
            {
                Console.Write("> ");
                string input = Console.ReadLine()?.Trim();
                if (input == null || input.Equals("exit", StringComparison.OrdinalIgnoreCase))
                    break;
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
            
            if (tokens.Length > 1 && string.Equals(tokens[0], "ffmpeg", StringComparison.OrdinalIgnoreCase))
            {
                potentialProgram = tokens[0];
                description = input.Substring(tokens[0].Length).Trim();
            }
            else
            {
                description = input;
            }
            
            // Get the generated command from the Azure OpenAI call
            string commandResult = CallOpenAIAPI(description);
            if (!string.IsNullOrEmpty(potentialProgram))
            {
                commandResult = potentialProgram + " " + commandResult;
            }
            
            // Output the generated command and allow inline editing
            Console.Write("Generated command: ");
            Console.Write(commandResult);
            
            // Inline editing simulation
            string editedCommand = commandResult;
            int cursorPos = commandResult.Length;
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
                }
                else if (!char.IsControl(key.KeyChar))
                {
                    editedCommand = editedCommand.Insert(cursorPos, key.KeyChar.ToString());
                    cursorPos++;
                    Console.Write(key.KeyChar);
                }
            }
            Console.WriteLine();
            Console.WriteLine("Accepted command: " + editedCommand);
            
            // Wait for Enter key to execute the command
            Console.WriteLine("Press Enter to execute the command, or any other key to cancel.");
            ConsoleKeyInfo execKey = Console.ReadKey(intercept: true);
            if (execKey.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                Console.WriteLine("Executing command...");
                ExecuteCommand(editedCommand);
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

                // Typically a single text part; take the first line just like before.
                string content = string.Empty;
                if (completion.Content != null && completion.Content.Count > 0)
                {
                    content = completion.Content[0].Text ?? string.Empty;
                }

                // Take the first non-empty line and sanitize common formatting.
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

                // Avoid returning an empty string (keeps UX consistent).
                if (content.Length == 0)
                {
                    return "generated_command_for_" + description.Replace(" ", "_");
                }

                return content;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Azure OpenAI call failed: " + ex.Message);
                return "generated_command_for_" + description.Replace(" ", "_");
            }
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