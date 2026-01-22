# Demo Skill

This is a demonstration skill that shows how to create custom skills for Kode.Agent.

## Overview

The demo skill provides two simple functions as examples:

1. **greet** - Greets a person by name
2. **calculate** - Performs basic math operations (add, subtract, multiply, divide)

## Usage Examples

### Using the greet function

```
User: "Use the demo skill to greet Alice"
Agent: Calls greet(name="Alice")
Result: "Hello, Alice! Welcome to Kode.Agent."
```

### Using the calculate function

```
User: "Calculate 10 plus 5 using the demo skill"
Agent: Calls calculate(a=10, b=5, operation="add")
Result: "10 add 5 = 15"
```

## How to Create Your Own Skills

### 1. Create a Skill Directory

```
skills/
└── my-skill/
    ├── skill.yaml       # Required: Skill definition
    └── README.md        # Optional: Documentation
```

### 2. Define skill.yaml

```yaml
name: my-skill
version: "1.0"
description: Description of what your skill does
author: Your Name

instructions: |
  Detailed instructions for the AI on how to use this skill.
  Explain when and how to use the functions.

functions:
  - name: my_function
    description: What this function does
    parameters:
      - name: param1
        type: string
        description: Description of parameter
        required: true
    code: |
      // JavaScript code here
      return `Result: ${param1}`;
```

### 3. Available Parameter Types

- `string` - Text values
- `number` - Numeric values
- `boolean` - true/false values
- `array` - List of values
- `object` - Complex objects

### 4. Writing Function Code

Functions use JavaScript syntax:

```javascript
// Access parameters directly
const result = a + b;

// Use JavaScript built-ins
const now = new Date();
const upper = text.toUpperCase();

// Return values
return result;

// Throw errors for invalid input
if (!validInput) {
  throw new Error("Invalid input");
}
```

### 5. Best Practices

✅ **DO:**
- Write clear, descriptive function names
- Provide detailed parameter descriptions
- Include error handling
- Add usage examples in instructions
- Keep functions focused and simple

❌ **DON'T:**
- Make network requests (use MCP tools instead)
- Access file system directly (use built-in tools)
- Create long-running operations
- Store state between calls

## Skill Configuration

Skills can be configured in `appsettings.json`:

```json
{
  "Skills": {
    "Paths": ["skills"],           // Directories to scan
    "Trusted": ["demo", "my-skill"] // Auto-activated skills
  }
}
```

## Debugging Skills

Enable detailed logging in `appsettings.Development.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Kode.Agent.Sdk.Core.Skills": "Debug"
    }
  }
}
```

## Example: Weather Skill

Here's a more practical example:

```yaml
name: weather
version: "1.0"
description: Get weather information (demo - returns mock data)
author: Kode.Agent Team

instructions: |
  Use this skill to get weather information for cities.
  Note: This is a demo that returns mock data.

functions:
  - name: get_weather
    description: Get current weather for a city
    parameters:
      - name: city
        type: string
        description: City name (e.g., "San Francisco")
        required: true
      - name: units
        type: string
        description: Temperature units (celsius or fahrenheit)
        required: false
    code: |
      const unitsType = units || 'celsius';
      const temp = unitsType === 'celsius' ? 22 : 72;
      const symbol = unitsType === 'celsius' ? '°C' : '°F';
      
      return `Weather in ${city}: Sunny, ${temp}${symbol}`;
```

## Integration with Agent

Skills are automatically loaded when:
1. They exist in configured skill paths
2. They have valid `skill.yaml` files
3. The Skills system is enabled in AgentConfig

The Agent will automatically discover and make these functions available as tools.

## Limitations

- Skills run in a sandboxed JavaScript environment
- No access to Node.js modules or npm packages
- No async/await support (synchronous only)
- Limited to computation and data transformation
- For external integrations, use MCP tools instead

## Getting Help

- Check the main README.md for configuration details
- See other examples in the `skills/` directory
- Refer to Kode.Agent.Sdk documentation

## Next Steps

1. Copy the demo skill as a template
2. Modify `skill.yaml` with your function definitions
3. Test with simple operations first
4. Add your skill to the "Trusted" list if needed
5. Document your skill with a README.md
