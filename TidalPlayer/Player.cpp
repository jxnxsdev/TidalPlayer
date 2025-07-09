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
    
}

void Player::play()
{
    
}

void Player::stop()
{
    
}

void Player::seek(float time)
{
    
}

void Player::listDevices()
{
    
}

void Player::setDevice(const std::string &deviceId, const std::string &mode)
{
    
}

void Player::setVolume(int volume)
{
    
}

void Player::loadMedia(std::string const& url, std::string const& encryptionKey, std::string const& streamFormat)
{
    stop();
    streamUrl = url;
    this->encryptionKey = encryptionKey;
    this->streamFormat = streamFormat;

    // TODO: Implement downloading of media
}

void Player::preloadMedia(std::string const& url, std::string const& encryptionKey, std::string const& streamFormat)
{
    preloadedStreamUrl = url;
    preloadedEncryptionKey = encryptionKey;
    preloadedStreamFormat = streamFormat;

    // TODO: Implement downloading of media
}


