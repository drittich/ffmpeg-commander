using System;

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

            var commandGenerator = new CommandGenerator();

            string commandArgs = isAdjustment
                ? commandGenerator.AdjustFromInstruction(s_lastAcceptedCommand!, description).GetAwaiter().GetResult()
                : commandGenerator.GenerateFromDescription(description).GetAwaiter().GetResult();

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
                var executor = new CommandExecutor();
                CommandExecutor.CommandExecutionResult result =
                    executor.ExecuteAsync(command).GetAwaiter().GetResult();

                Console.WriteLine("Exit code: " + result.ExitCode);
                Console.WriteLine("Command output:");
                Console.WriteLine(result.StdOut);

                if (!string.IsNullOrEmpty(result.StdErr))
                {
                    Console.WriteLine("Command error:");
                    Console.WriteLine(result.StdErr);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error executing command: " + ex.Message);
            }
        }
    }
}