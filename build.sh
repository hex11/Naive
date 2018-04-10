#!/bin/bash
set -x

SOLUTION_NAME=Naive.sln

BUILDINFO_FILE="NaiveSocksCliShared/BuildInfo.cs"

BUILD_TIME=$(date -u +'%Y-%m-%d %H:%M:%S UTC')

SHORT_COMMIT=$(git log --format=%h -1)

if [[ TRAVIS ]]; then
	if [[ $TRAVIS_TAG == v* ]]; then
		:
		# sed -i "s/ Version = \".*\";/ Version = \"${TRAVIS_TAG#v}\";/" "$BUILDINFO_FILE"
	elif [[ $TRAVIS_TAG == "" ]]; then
		git config --local user.name "hex11"
		git config --local user.email "hekusu11@gmail.com"
	    git tag "b-$(date +'%Y%m%d%-H%M%S')"
		# sed -i "s/ Version = \".*\";/ Version = \"0.3.$(date +'%y%j.%H%M')\";/" "$BUILDINFO_FILE"
	fi

	buildText="git"
	# buildText=" repo $TRAVIS_REPO_SLUG"
	# if [[ $TRAVIS_PULL_REQUEST_SLUG != "" ]]; then
	# 	buildText+=" PR $TRAVIS_PULL_REQUEST_SLUG"
	# fi
	buildText+=" branch $TRAVIS_BRANCH commit $SHORT_COMMIT built at $BUILD_TIME"
	sed -i "s/BuildText = \".*\";/BuildText = \"$buildText\";/" "$BUILDINFO_FILE"
fi

nuget restore "$SOLUTION_NAME"
dotnet restore

msbuild /p:Configuration=Release $SOLUTION_NAME

if [[ "$1" == "--deploy" ]]; then
	pushd NaiveSocks/NaiveSocks/
	bash build_deploy.sh -u ../../bin/to_upload
	popd
fi
