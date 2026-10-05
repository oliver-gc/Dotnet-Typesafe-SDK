using TypesafeSdk;
using TypesafeSdk.Models;

class Program
{
    static async Task Main(string[] args)
    {
        // Reads TYPESAFE_API_KEY from the environment.
        using var client = new TypeSafeClient(new TypeSafeClientOptions
        {
            LogLevel = TypeSafeLogLevel.Info,
        });

        var response = await client.SystemOneAsync(
            state: "I was charged twice. Please help ASAP.",
            questions: new Dictionary<string, Question>
            {
                ["billing"] = new NoulQuestion { Instructions = "Is this about billing?" },
                ["tone"] = new ChoiceQuestion
                {
                    Instructions = "What is the tone?",
                    Criteria = new Dictionary<string, object?> { ["calm"] = null, ["angry"] = null },
                },
                ["urgency"] = new ScoreQuestion
                {
                    Instructions = "How urgent is this?",
                    Criteria = ["low", "medium", "high"],
                },
            });

            
        Console.WriteLine(response.Model);
        Console.WriteLine(response.Nouls["billing"].Noul);
        Console.WriteLine(response.Choices["tone"].Choice);
        Console.WriteLine(response.Scores["urgency"].Score);
    }
}
