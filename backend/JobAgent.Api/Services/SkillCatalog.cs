using System.Text.RegularExpressions;

namespace JobAgent.Api.Services;

/// <summary>Known technical skills with their common aliases, used to read skills out of resumes and job descriptions.</summary>
public static class SkillCatalog
{
    // Canonical name first, then aliases. Aliases are matched as whole words, case-insensitively.
    private static readonly string[][] Entries =
    [
        ["C#", "c#", "csharp", "c sharp"], [".NET", ".net", "dotnet", ".net core", ".net framework", ".net 8", ".net 6"],
        ["ASP.NET", "asp.net", "asp.net core", "asp.net mvc", "aspnet"], ["Entity Framework", "entity framework", "ef core"],
        ["LINQ", "linq"], ["Blazor", "blazor"], ["WPF", "wpf"], ["WinForms", "winforms", "windows forms"], ["Xamarin", "xamarin"], [".NET MAUI", "maui"],
        ["Java", "java"], ["Spring", "spring boot", "spring framework", "spring"], ["Kotlin", "kotlin"], ["Scala", "scala"],
        ["Python", "python"], ["Django", "django"], ["Flask", "flask"], ["FastAPI", "fastapi"],
        ["JavaScript", "javascript", "ecmascript"], ["TypeScript", "typescript"], ["Node.js", "node.js", "nodejs", "node"],
        ["React", "react", "react.js", "reactjs"], ["Next.js", "next.js", "nextjs"], ["Angular", "angular", "angularjs"], ["Vue", "vue", "vue.js", "vuejs"],
        ["Svelte", "svelte"], ["Redux", "redux"], ["HTML", "html", "html5"], ["CSS", "css", "css3"], ["Tailwind", "tailwind", "tailwindcss"], ["Sass", "sass", "scss"],
        ["jQuery", "jquery"], ["Webpack", "webpack"], ["Vite", "vite"],
        ["Go", "golang", "go"], ["Rust", "rust"], ["C++", "c++", "cpp"], ["C", "c programming", "ansi c"], ["Ruby", "ruby"], ["Rails", "rails", "ruby on rails"],
        ["PHP", "php"], ["Laravel", "laravel"], ["Swift", "swift"], ["Objective-C", "objective-c"], ["iOS", "ios"], ["Android", "android"],
        ["React Native", "react native"], ["Flutter", "flutter"], ["Dart", "dart"],
        ["SQL", "sql"], ["SQL Server", "sql server", "mssql", "ms sql", "t-sql", "tsql"], ["PostgreSQL", "postgresql", "postgres"], ["MySQL", "mysql"],
        ["Oracle", "oracle", "pl/sql"], ["MongoDB", "mongodb", "mongo"], ["Redis", "redis"], ["Cassandra", "cassandra"], ["DynamoDB", "dynamodb"],
        ["Cosmos DB", "cosmos db", "cosmosdb"], ["Elasticsearch", "elasticsearch", "opensearch"], ["Snowflake", "snowflake"], ["BigQuery", "bigquery"],
        ["Redshift", "redshift"], ["Databricks", "databricks"], ["Spark", "spark", "apache spark", "pyspark"], ["Kafka", "kafka"], ["RabbitMQ", "rabbitmq"],
        ["Airflow", "airflow"], ["dbt", "dbt"], ["Hadoop", "hadoop"], ["ETL", "etl"], ["GraphQL", "graphql"], ["REST", "rest", "restful", "rest api", "rest apis"],
        ["gRPC", "grpc"], ["Microservices", "microservices", "micro-services"], ["Event-driven", "event-driven", "event driven"],
        ["AWS", "aws", "amazon web services"], ["Azure", "azure", "microsoft azure"], ["GCP", "gcp", "google cloud"], ["Lambda", "aws lambda", "lambda"],
        ["Azure Functions", "azure functions"], ["Azure DevOps", "azure devops"], ["Docker", "docker"], ["Kubernetes", "kubernetes", "k8s", "aks", "eks", "gke"],
        ["Terraform", "terraform"], ["Ansible", "ansible"], ["Helm", "helm"], ["CI/CD", "ci/cd", "cicd", "continuous integration"], ["Jenkins", "jenkins"],
        ["GitHub Actions", "github actions"], ["GitLab CI", "gitlab ci"], ["Git", "git"], ["Linux", "linux", "unix"], ["Bash", "bash", "shell scripting"],
        ["PowerShell", "powershell"], ["Serverless", "serverless"], ["Observability", "observability"], ["Datadog", "datadog"], ["Prometheus", "prometheus"],
        ["Grafana", "grafana"], ["Splunk", "splunk"], ["New Relic", "new relic"],
        ["Machine Learning", "machine learning", "ml"], ["Deep Learning", "deep learning"], ["LLM", "llm", "llms", "large language models"],
        ["Generative AI", "generative ai", "genai"], ["NLP", "nlp", "natural language processing"], ["Computer Vision", "computer vision"],
        ["PyTorch", "pytorch"], ["TensorFlow", "tensorflow"], ["scikit-learn", "scikit-learn", "sklearn"], ["Pandas", "pandas"], ["NumPy", "numpy"],
        ["RAG", "rag", "retrieval augmented generation"], ["MLOps", "mlops"],
        ["Unit Testing", "unit testing", "unit tests"], ["xUnit", "xunit"], ["NUnit", "nunit"], ["Jest", "jest"], ["Cypress", "cypress"], ["Playwright", "playwright"],
        ["Selenium", "selenium"], ["TDD", "tdd", "test-driven"], ["Agile", "agile"], ["Scrum", "scrum"], ["System Design", "system design", "distributed systems"],
        ["OOP", "oop", "object-oriented", "object oriented"], ["Design Patterns", "design patterns"], ["SOLID", "solid principles"],
        ["OAuth", "oauth", "oauth2", "openid connect", "oidc"], ["Security", "application security", "appsec", "owasp"],
        ["Salesforce", "salesforce", "apex"], ["SAP", "sap"], ["ServiceNow", "servicenow"], ["Power BI", "power bi", "powerbi"], ["Tableau", "tableau"], ["Excel", "excel"],
        ["Figma", "figma"], ["Jira", "jira"],
    ];

    private static readonly (string Canonical, Regex Pattern)[] Compiled = Entries
        .Select(e => (e[0], new Regex(@"(?<![a-z0-9+#.])(" + string.Join("|", e.Skip(1).OrderByDescending(a => a.Length).Select(Regex.Escape)) + @")(?![a-z0-9+#]|\.[a-z0-9])",
            RegexOptions.IgnoreCase | RegexOptions.Compiled)))
        .ToArray();

    // Aliases that are ordinary English words; only trust them when written the way the technology is written.
    private static readonly HashSet<string> CaseSensitive = ["Go", "Rust", "Swift", "Spring", "Node.js", "REST", "Excel", "Oracle", "Lambda", "Helm", "Dart", "React", "Git", "RAG", "Spark", "SAP"];

    // How much a skill says about whether a job fits: the language/framework a job is built in matters far more
    // than process words nearly every posting mentions (Agile, Git, Jira...).
    private static readonly HashSet<string> CoreSkills = new(StringComparer.OrdinalIgnoreCase)
    {
        "C#", ".NET", "ASP.NET", "Entity Framework", "LINQ", "Blazor", "WPF", "WinForms", "Xamarin", ".NET MAUI", "Java", "Spring", "Kotlin", "Scala",
        "Python", "Django", "Flask", "FastAPI", "JavaScript", "TypeScript", "Node.js", "React", "Next.js", "Angular", "Vue", "Svelte", "Redux",
        "Go", "Rust", "C++", "C", "Ruby", "Rails", "PHP", "Laravel", "Swift", "Objective-C", "iOS", "Android", "React Native", "Flutter", "Dart",
        "Salesforce", "SAP", "ServiceNow", "Machine Learning", "Deep Learning", "PyTorch", "TensorFlow", "Computer Vision", "NLP", "LLM",
    };
    private static readonly HashSet<string> GeneralSkills = new(StringComparer.OrdinalIgnoreCase)
    {
        "Agile", "Scrum", "Git", "Jira", "CI/CD", "Unit Testing", "TDD", "OOP", "Design Patterns", "SOLID", "System Design", "REST",
        "Microservices", "Event-driven", "Excel", "Linux", "Bash", "Security", "Observability", "Figma", "HTML", "CSS", "SQL",
    };

    /// <summary>3 for languages/frameworks, 1 for general practices, 2 for platforms (cloud, databases, tooling).</summary>
    public static int Weight(string skill) => CoreSkills.Contains(skill) ? 3 : GeneralSkills.Contains(skill) ? 1 : 2;

    public static bool IsCore(string skill) => CoreSkills.Contains(skill);

    public static IReadOnlyList<string> Extract(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return [];
        var found = new List<string>();
        foreach (var (canonical, pattern) in Compiled)
        {
            var m = pattern.Match(text);
            if (!m.Success) continue;
            // e.g. "Go" but not "go" or "Go-to-market".
            if (CaseSensitive.Contains(canonical) && !pattern.Matches(text).Any(x => x.Value.Any(char.IsUpper) && !IsFollowedByHyphen(text, x))) continue;
            found.Add(canonical);
        }
        return found;
    }

    private static bool IsFollowedByHyphen(string text, Match m) => m.Index + m.Length < text.Length && text[m.Index + m.Length] == '-';

    /// <summary>Maps user-entered skills to canonical names where known ("dotnet" → ".NET"); unknown skills are kept as typed.</summary>
    public static IReadOnlyList<string> Normalize(IEnumerable<string> skills) =>
        skills.Select(s => s.Trim()).Where(s => s.Length > 0)
            .Select(s => Compiled.FirstOrDefault(c => c.Pattern.Match(s) is { Success: true } m && m.Length == s.Length).Canonical ?? s)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
