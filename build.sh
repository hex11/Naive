#!/bin/bash
set -x

SOLUTION_NAME=Naive.sln

BUILDINFO_FILE="NaiveSocksCliShared/BuildInfo.cs"

MSBUILD=msbuild

function action_pre-build() {
	BUILD_TIME=$(date -u +'%Y-%m-%d %H:%M:%S UTC')
	SHORT_COMMIT=$(git log --format=%h -1)
	if [[ $TRAVIS ]]; then
		buildText="git $TRAVIS_BRANCH $SHORT_COMMIT"
		[[ $BUILD_TIME ]] && buildText+=" built at $BUILD_TIME"
		sed -i "s/BuildText = \".*\";/BuildText = \"$buildText\";/" "$BUILDINFO_FILE"
	elif [[ $APPVEYOR ]]; then
		buildText="git $APPVEYOR_REPO_BRANCH $SHORT_COMMIT $APPVEYOR_BUILD_VERSION"
		[[ $BUILD_TIME ]] && buildText+=" built at $BUILD_TIME"
		sed -i "s/BuildText = \".*\";/BuildText = \"$buildText\";/" "$BUILDINFO_FILE"
		MSBUILD=MSBuild.exe
	fi
	nuget restore "$SOLUTION_NAME" || return 1
	dotnet restore || return 1
}


function action_msbuild() {
	$MSBUILD /m "$SOLUTION_NAME" '/p:Configuration=Release' || return 1
}

function action_build() {
	action_pre-build
	action_msbuild
}

function action_deploy() {
	pushd NaiveSocks/NaiveSocks/
	bash build_deploy.sh -u ../../bin/upload || return 1
	popd
	pushd NaiveSocksDotNetCore/
	bash build_deploy.sh -u ../bin/upload || return 1
	popd
}

function action_all() {
	action_build || return 1
	action_deploy || return 1
}

for var in "$@"
do
	action_$var || exit 1
done
