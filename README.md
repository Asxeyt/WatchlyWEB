Watchly

Watchly is a media tracker for people who consume too much stuff to keep in their head. Movies, TV shows, anime, manga, games, books, comics, cartoons - log what you've watched or read, rate it, write a review, and keep a watchlist for what's next.

It started life as a smaller project called KategoriSeçici and grew into something worth a real name.

Features
Track movies, TV series, anime, manga, games, books, comics, and cartoons in one place
Rate and review anything on your list
Interactive quizzes for recommendations when you don't know what to watch or read next
Watchlist with reminders so you don't lose track of what you meant to get to
Google sign-in or email/password, with email verification
User profiles with avatar and cover images
Tech stack
ASP.NET Core MVC (C#, .NET 8)
PostgreSQL in production (Neon), SQLite for local dev
Entity Framework Core
Docker for deployment
Bootstrap, vanilla JS on the frontend
Running it locally

Clone it:

bash
git clone https://github.com/Asxeyt/WatchlyWEB.git
cd WatchlyWEB

Run it:

bash
dotnet run

By default it falls back to a local SQLite database, so this works out of the box with no extra setup. A few things are optional and only needed if you want the full feature set:

Feature	Environment variable(s)
PostgreSQL instead of SQLite	DATABASE_URL (or NEON_DATABASE_URL)
Google sign-in	GOOGLE_CLIENT_ID, GOOGLE_CLIENT_SECRET
Verification emails	SMTP_HOST, SMTP_PORT, SMTP_USER, SMTP_PASS, SMTP_FROM
Movie search	OMDB_API_KEY

Without SMTP configured, email verification still works, it just won't be able to send anything.

Docker
bash
docker build -t watchly .
docker run -p 10000:10000 watchly
Live

kategorisecici.onrender.com - yes, the URL still says the old name, haven't gotten around to moving it.

Made by Asxeyt :)
