#pragma once
#include <string>
#include "Player.h"

class SignalHandler
{
public:

    void handleSignal(const std::string& signal);

    Player const &player;

    SignalHandler(Player const &playerInstance) : player(playerInstance) {}
};
