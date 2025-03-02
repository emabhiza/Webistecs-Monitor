# Use a .NET SDK image for building
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /app

# Copy the project file
COPY ["Webistecs-Monitor.csproj", "./"]

# Copy only existing folders
COPY ["BackupTools/", "BackupTools/"]
COPY ["Configuration/", "Configuration/"]
COPY ["Domain/", "Domain/"]
COPY ["Google/", "Google/"]
COPY ["Logging/", "Logging/"]
COPY ["Grafana/", "Grafana/"]

# Copy specific required files
COPY ["Program.cs", "./"]
COPY ["Constants.cs", "./"]
COPY ["appsettings.json", "./"]

# Restore dependencies
RUN dotnet restore

# Build the app
RUN dotnet build --configuration Release --no-restore

# Publish the app
RUN dotnet publish --configuration Release --no-restore -o /app/out

# Use a lightweight ASP.NET runtime image for final build
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app/out .

# Install dependencies required for Selenium and ChromeDriver
RUN apt-get update && apt-get install -y \
    chromium \
    chromium-driver \
    libnss3 \
    libatk-bridge2.0-0 \
    libxcomposite1 \
    libxdamage1 \
    libxrandr2 \
    libgbm1 \
    libasound2 \
    libpangocairo-1.0-0 \
    libpango-1.0-0 \
    libcups2 \
    fonts-liberation \
    libgtk-3-0 \
    libx11-xcb1 \
    xdg-utils \
    && apt-get clean \
    && rm -rf /var/lib/apt/lists/*

# Create symlink for Chromium
RUN ln -s /usr/bin/chromium /usr/bin/chromium-browser

# Verify Chromium and ChromeDriver installation
RUN which chromium-browser && chromium-browser --version
RUN which chromedriver && chromedriver --version

# Set environment variable to tell Selenium to use system Chromium
ENV CHROME_BIN=/usr/bin/chromium-browser

# Set up entrypoint to run the .NET application
ENTRYPOINT ["dotnet", "Webistecs-Monitor.dll"]