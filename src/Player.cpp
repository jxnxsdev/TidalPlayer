#include "Player.h"

void Player::sendProgressUpdate()
{
    if (state == State::Active) {
        nlohmann::json progressUpdate;
        progressUpdate["signal"] = "media.currenttime";
        progressUpdate["time"] = progress;
        progressUpdate["url"] = streamUrl;
        signalSender.sendResponseSignal(progressUpdate.dump());
    }
}

void Player::pause()
{
    // TODO: Implement pausing of the media stream

    nlohmann::json pauseSignal;
    pauseSignal["signal"] = "media.state";
    pauseSignal["state"] = "paused";
    pauseSignal["url"] = streamUrl;
    signalSender.sendResponseSignal(pauseSignal.dump());
    state = State::Paused;
}

void Player::play()
{
    // TODO: Implement playing of the media stream

    nlohmann::json playSignal;
    playSignal["signal"] = "media.state";
    playSignal["state"] = "active";
    playSignal["url"] = streamUrl;
    signalSender.sendResponseSignal(playSignal.dump());
    state = State::Active;
}

void Player::stop()
{
    // TODO: Implement stopping of the media stream

    nlohmann::json stopSignal;
    stopSignal["signal"] = "media.state";
    stopSignal["state"] = "stopped";
    stopSignal["url"] = streamUrl;
    signalSender.sendResponseSignal(stopSignal.dump());
    state = State::Stopped;
    progress = 0.0f;
}

void Player::seek(float time)
{
    // TODO: Implement seeking in the media stream

    progress = time;
}

void Player::listDevices()
{
    // TODO: Implement listing of available devices
}

void Player::setDevice(const std::string &deviceId, const std::string &mode)
{

    // TODO: Implement setting the device for playback

    this->deviceId = deviceId;
    if (mode == "shared") {
        deviceMode = DeviceMode::Shared;
    } else if (mode == "exclusive") {
        deviceMode = DeviceMode::Exclusive;
    } else {
        std::cerr << "Invalid device mode: " << mode << std::endl;
        return;
    }
}

void Player::setVolume(int volume)
{
    // TODO: Implement setting the volume

    this->volume = volume;
}

void Player::loadMedia(std::string const& url, std::string const& encryptionKey, std::string const& streamFormat)
{
    stop();
    streamUrl = url;
    this->encryptionKey = encryptionKey;
    this->streamFormat = streamFormat;

    nlohmann::json loadSignal;
    loadSignal["signal"] = "media.duration";
    loadSignal["duration"] = streamDuration;
    loadSignal["url"] = streamUrl;
    
    // TODO: Implement downloading of media
}

void Player::preloadMedia(std::string const& url, std::string const& encryptionKey, std::string const& streamFormat)
{
    preloadedStreamUrl = url;
    preloadedEncryptionKey = encryptionKey;
    preloadedStreamFormat = streamFormat;


    
    // TODO: Implement downloading of media
}


void Player::transitionNextSong()
{
    stop();

    // TODO: Implement transitioning to the next song

    streamUrl = preloadedStreamUrl;
    encryptionKey = preloadedEncryptionKey;
    streamFormat = preloadedStreamFormat;
    streamDuration = preloadedStreamDuration;
    preloadedStreamUrl.clear();
    preloadedEncryptionKey.clear();
    preloadedStreamFormat.clear();
    preloadedStreamDuration = 0.0f;

    nlohmann::json transitionSignal;
    transitionSignal["signal"] = "media.duration";
    transitionSignal["duration"] = streamDuration;
    transitionSignal["url"] = streamUrl;
    signalSender.sendResponseSignal(transitionSignal.dump());
}
