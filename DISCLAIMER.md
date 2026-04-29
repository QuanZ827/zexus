# Disclaimer

## Independent project

Zexus is an **independent, personal project** developed and maintained by Zhequan Zhang. It is not affiliated with, endorsed by, or sponsored by any employer, organization, or company.

## No affiliation

- This project is **not affiliated with Autodesk, Inc.** "Revit" and "Autodesk" are registered trademarks of Autodesk, Inc. All references to Revit are for interoperability purposes only.
- This project is **not affiliated with Anthropic, PBC.** "Claude" is a product of Anthropic. Zexus uses the Claude API as a third-party service; Anthropic does not endorse this project.
- This project is **not affiliated with OpenAI, Inc.** "GPT" is a product of OpenAI. Zexus uses the OpenAI API as a third-party service; OpenAI does not endorse this project.
- This project is **not affiliated with Google LLC.** "Gemini" is a product of Google. Zexus uses the Google AI API as a third-party service; Google does not endorse this project.
- This project does **not represent the views, products, or intellectual property of any employer**, past or present.

## Use at your own risk

This software is provided "AS IS", without warranty of any kind, express or implied. By using Zexus, you acknowledge:

- **Model modifications are irreversible.** Always save your Revit project before using AI-assisted automation. The software includes confirmation prompts for write operations, but the user bears full responsibility for reviewing changes.
- **AI outputs are not guaranteed.** The AI generates code and responses based on probabilistic language modeling. Results may be incorrect, incomplete, or unexpected. Always verify AI-generated actions before applying them to production models.
- **API costs are your responsibility.** Zexus connects to your chosen LLM provider's API (Anthropic, OpenAI, or Google) using your own API key. You are responsible for all associated usage costs.
- **No professional engineering advice.** This tool does not provide engineering, architectural, or construction advice. All outputs should be reviewed by qualified professionals.

## Data and privacy

- Zexus sends Revit model metadata (element names, parameter values, categories) to your chosen LLM provider's API for processing. **No data is stored by Zexus on any external server.**
- Your API key is stored locally on your machine and is never transmitted to any server other than your selected provider's API endpoint.
- Local session logs (if generated) are written to `%APPDATA%\Zexus\` and are not uploaded.

## License

Zexus is licensed under the [Apache License, Version 2.0](LICENSE).
