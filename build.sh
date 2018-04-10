#!/bin/bash

set SOLUTION_NAME = Naive.sln

nuget restore $SOLUTION_NAME
dotnet restore

msbuild /p:Configuration=Release $SOLUTION_NAME
