# Putting JobAgent online for friends

The repo root has a `Dockerfile` that builds the frontend and the API into one image. Any host that runs Docker
containers and offers PostgreSQL works (Render, Railway, Fly.io, a VPS…). The steps below use generic names.

## What you need
1. **A PostgreSQL database.** The host usually gives you a connection URL.
2. **A web service built from the `Dockerfile`**, with a **persistent disk mounted at `/app/uploads`**
   (that is where resumes and tailored resumes are stored; without a disk they vanish on every redeploy).
3. **The service must stay awake.** Job scans run on a timer inside the app, so a free plan that sleeps when idle
   will stop scanning. Plan on a small paid instance plus the database (roughly $10–20/month; check the host's current prices).

## Environment variables
| Variable | What it is |
|---|---|
| `DATABASE_URL` | PostgreSQL URL from your host (`postgres://user:pass@host:port/db`). |
| `JWT_KEY` | A long random string (32+ characters) that signs logins. Keep it secret; changing it logs everyone out. |
| `ANTHROPIC_API_KEY` | Your Anthropic key, used for everyone's resume tailoring. |
| `Auth__InviteCode` | A code friends must enter when creating an account. **Set this**, otherwise anyone with the link can sign up and spend your AI credit. |
| `Ai__MonthlyTailorings` | Resume tailorings per friend per month (default 30). The admin is unlimited. |
| `ADZUNA_APP_ID`, `ADZUNA_APP_KEY` | Optional: extra job source (free at developer.adzuna.com). |

`Automation__Enabled=false` and `PORT` are already set by the Dockerfile.

## First run
1. Open the site and **create your own account first**. The first account becomes the admin: it takes over any
   existing data and is the only one that can manage the company list, Adzuna keys and the AI key.
2. Send friends the link and the invite code. They sign up, upload their resume (a .docx works best for tailoring),
   adjust their job preferences, and their feed fills with jobs matched to their tech stack.

## The browser extension
Friends load the `extension` folder in Chrome (`chrome://extensions` → Developer mode → Load unpacked), click the
extension, enter the site's address plus their email and password, and use **Autofill this page** on application forms.

## Running it on your own computer instead
`start-app.bat` still works. You'll be asked to create an account the first time; it takes over your existing profile.
