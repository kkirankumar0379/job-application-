# Job Application Agent — MVP 0.1

A starter application for employer/ATS job applications. This version focuses on external company career pages and requires explicit user approval before final submission.

## Implemented
- ASP.NET Core 10 minimal API
- PostgreSQL-ready EF Core data model
- Candidate profile storage
- Basic JD matching
- Playwright browser automation in headed mode
- ATS detection for Greenhouse, Lever, Workday, iCIMS, SmartRecruiters, and generic pages
- Safe autofill for common contact fields
- Resume upload
- Unknown required-field detection
- Direct LinkedIn-domain automation block
- Explicit final-submit approval endpoint
- React/Vite dashboard starter

## Run
1. `docker compose up -d`
2. `cd backend/JobAgent.Api && dotnet restore`
3. Create/update EF migrations and database.
4. Build and install Playwright Chromium.
5. Run API on http://localhost:5000.
6. `cd frontend && npm install && npm run dev`

## Safety
The agent does not invent unknown application answers and does not automate LinkedIn directly. CAPTCHA/login and sensitive or unknown questions remain manual. Final Submit requires explicit approval.

## Next
Candidate Brain + saved answers, Greenhouse adapter, Lever adapter, Workday adapter, AI JD parser, resume selection/tailoring, application tracker, and email status ingestion.
