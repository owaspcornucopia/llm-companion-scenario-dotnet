# Contributing with Claude AI Assistant

This guide helps contributors use Claude (an AI assistant) effectively when working on OWASP Cornucopia, while maintaining code quality and adhering to [project standards](/.github/copilot-instructions.md).

> This document is supposed to be the primary source of context for **all** AI tools. Context files of tools other than Claude should refer to [this `CLAUDE.md` file](CLAUDE.md) for detailed guidelines. This is already the case for:
> * GitHub CoPilot ([`.github/copilot-instructions.md`](../.github/copilot-instructions.md))

## Essential Guidelines

### 1. Commit Sign-off

All commits must be signed off (DCO):

```bash
git commit -s -m "Your commit message"
```

### 2. Branch and PR Strategy

- Keep PRs focused on a single scope
- Reference related issues in PR descriptions

## Development Workflow with Claude

### 3. Understanding the Codebase

```
Ask Claude to:
- Explain specific components or patterns
- Identify where to implement new features
- Trace code execution paths
```

### 4. Implementation

```
Ask Claude to:
- Generate initial implementation
- Suggest test cases
- Review for security implications
```

### 5. Quality Assurance

```
Before committing:
2. For Python code, run: pipenv run black --line-length=120 --check .
3. Run relevant test suites
4. Manually verify functionality
5. Check for unintended changes
6. Follow the [project standards](/.github/copilot-instructions.md)
```

## Anti-Patterns to Avoid

❌ **Don't**: Accept AI suggestions blindly without understanding them
✅ **Do**: Review and understand all AI-generated code

❌ **Don't**: Submit PRs with verbose AI-generated comments
✅ **Do**: Clean up and keep only meaningful comments

❌ **Don't**: Skip testing because AI "seems confident"
✅ **Do**: Always run the full test suite

❌ **Don't**: Use AI for contribution farming or trivial changes
✅ **Do**: Make meaningful contributions that add value


## Quality Checklist

Before submitting a Claude-assisted PR:

- [ ] Code follows the project coding standard and style
- [ ] Manual testing completed
- [ ] Commits are signed off
- [ ] Single, focused scope
- [ ] All CI checks passing
