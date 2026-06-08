FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY *.csproj .
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
# Magick.NET (HEIC/HEIF -> JPEG transcode, #220) bundles its own ImageMagick + libheif natives but
# depends on the system OpenMP runtime (libgomp1), which the slim aspnet image doesn't ship.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libgomp1 \
    && rm -rf /var/lib/apt/lists/*
# Deliberately NOT installing libjpeg, despite `ldd` reporting "libjpeg.so.8 => not found" on
# QuestPDF's bundled libqpdf.so. It's benign: QuestPDF (2024.12.3) renders AND merges PDFs entirely
# through libQuestPdfSkia.so (all deps resolved); libqpdf.so is never loaded by any path we use.
# Verified in a prod-identical container (this image, libjpeg absent) that our text-only audit/vendor
# PDF exports — and even QuestPDF.Document.Merge — both succeed, with libjpeg never even searched for
# (LD_DEBUG trace shows only libQuestPdfSkia.so loaded). qpdf's libjpeg dependency would only matter
# for JPEG-in-PDF qpdf operations CompliDrop doesn't perform (image embedding goes through Skia). So
# adding libjpeg would install a dependency for an unused .so — and Debian ships .so.62, not .so.8.
COPY --from=build /app .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "CompliDrop.Api.dll"]
