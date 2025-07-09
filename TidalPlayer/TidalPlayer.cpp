#include <iostream>
#include "SignalHandler.h"



int main()
{
    auto signalHandler = SignalHandler();

    signalHandler.sendResponseSignal("{\"signal\":\"version\",\"version\":\"1.0.0\"}");

    std::string input;
    while (std::getline(std::cin, input)) {
        signalHandler.handleSignal(input);
    }
    return 0;
}