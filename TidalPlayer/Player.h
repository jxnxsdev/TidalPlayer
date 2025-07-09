#pragma once

#include "SignalSender.h"
#include "includes/json.hpp"

#include <string>

class Player
{
public:
    enum class State
    {
        Stopped,
        Paused,
        Active
    };
    
    std::string encryptionKey;
    std::string streamFormat;
    std::string streamUrl;
    float streamDuration = 0.0f;

    std::string preloadedEncryptionKey;
    std::string preloadedStreamFormat;
    std::string preloadedStreamUrl;
    float preloadedStreamDuration = 0.0f;

    State state = State::Stopped;
    int volume = 1;
    float progress = 0.0f;

    SignalSender &signalSender;


    Player(SignalSender &signalSenderInstance)
        : signalSender(signalSenderInstance) {}

    void pause();
    void play();
    void stop();

    void seek(float time);

    void listDevices();
    void setDevice(const std::string &deviceId, const std::string &mode);

    void setVolume(int volume);

    void loadMedia(std::string const &url, std::string const &encryptionKey,
                   std::string const &streamFormat);

    void preloadMedia(std::string const &url, std::string const &encryptionKey,
                      std::string const &streamFormat);
    
private:
    void sendProgressUpdate();
};
