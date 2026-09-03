# DeviceMaster headless (Linux) — Docker image for the container control loop.
#
#   docker build -t devicemaster:headless .
#   docker run -d --name devicemaster --privileged --restart unless-stopped \
#       -v /dev:/dev \
#       -v /mnt/user/appdata/devicemaster:/config \
#       -e DEVICEMASTER_CONFIG=/config/config.json \
#       devicemaster:headless
#
# The image is self-contained (no .NET runtime needed on the host). nvidia-smi is NOT in the
# image — mount the host binary if GPU temperature is wanted:
#       -v /usr/bin/nvidia-smi:/usr/bin/nvidia-smi:ro
#
# NOTE: the headless app shares its device sessions and safety code with the Windows app but
# is built for the linux-x64 RID. The shared projects target net9.0-windows (API-surface only —
# the executable never calls Windows APIs).

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY . .
# EnableWindowsTargeting: building the net9.0-windows TFM on a non-Windows build host.
RUN dotnet publish src/DeviceMaster.App.Headless/DeviceMaster.App.Headless.csproj \
    -c Release -r linux-$TARGETARCH --self-contained true \
    -p:EnableWindowsTargeting=true \
    -p:PublishSingleFile=false \
    -o /publish

FROM debian:bookworm-slim
# libfontconfig1/libfreetype6: System.Drawing (the LCD metric renderer) needs fontconfig.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libfontconfig1 libfreetype6 \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /publish/ ./
ENV DEVICEMASTER_CONFIG=/config/config.json
VOLUME ["/config"]
# SIGTERM is handled in-process (hubs are restored to hardware mode on stop) — the default
# docker stop timeout (10 s) is plenty for the graceful path.
ENTRYPOINT ["/app/DeviceMaster.App.Headless"]
CMD ["loop"]
