using System;
using System.Diagnostics;
using System.Threading;

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
            
            // Get the generated command from the simulated OpenAI API call
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
            // Simulated API call: returns a dummy command based on the description.
            return "generated_command_for_" + description.Replace(" ", "_");
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