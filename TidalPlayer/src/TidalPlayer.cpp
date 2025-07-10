#include <iostream>
#include "SignalHandler.h"
#include "Player.h"
#include "SignalSender.h"



int main()
{
    auto signalSender = SignalSender();
    auto player = Player(signalSender);
    auto signalHandler = SignalHandler(player);

    signalSender.sendResponseSignal("{\"signal\":\"version\",\"version\":\"1.0.0\"}");

    std::string input;
    while (std::getline(std::cin, input)) {
        signalHandler.handleSignal(input);
    }
    return 0;
}