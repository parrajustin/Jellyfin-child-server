/*
 * Jellyfin child server: web client plugin.
 *
 * Served by the child server and injected into the web client's index page before the
 * application bundle. The web client's plugin loader finds "ChildServerPlugin" on window
 * (it is listed in the served config.json) and registers the class it returns as a
 * pre-play interceptor: before any playback, it asks the server whether the item is on
 * this device and whether the parent server answers, and shows a plain message instead of
 * a failed player when the parent cannot be reached.
 */
(function () {
    'use strict';

    var TITLE = "Can't connect to parent server";
    var MEDIA_TYPES = { Episode: true, Movie: true, Video: true, MusicVideo: true };

    function isCandidate(item) {
        return !!item && !!item.Id && MEDIA_TYPES[item.Type] === true;
    }

    function messageFor(item) {
        var name = item && item.Name ? '"' + item.Name + '"' : 'This video';
        return name + ' is not stored on this device and the parent server cannot be reached. '
            + 'Try again when the parent server is back online.';
    }

    window.ChildServerPlugin = async function () {
        return class ChildServerPreplayInterceptor {
            constructor(dependencies) {
                this.name = 'Child server';
                this.id = 'childserver';
                this.type = 'preplayintercept';
                this.priority = -1;
                this.dependencies = dependencies || {};
            }

            intercept(options) {
                var item = options && options.item;
                var apiClient = window.ApiClient;
                if (!isCandidate(item) || !apiClient) {
                    return Promise.resolve();
                }

                var self = this;
                var url = apiClient.getUrl('ChildServer/Items/' + encodeURIComponent(item.Id) + '/Availability');
                return apiClient.getJSON(url).then(function (availability) {
                    if (availability && availability.IsManaged && !availability.IsCached && !availability.ParentReachable) {
                        self.showUnavailable(item);
                        return Promise.reject(new Error(TITLE));
                    }
                }, function () {
                    // Availability unknown: let playback proceed and the server decide.
                });
            }

            showUnavailable(item) {
                var dashboard = this.dependencies.dashboard || window.Dashboard;
                if (dashboard && typeof dashboard.alert === 'function') {
                    dashboard.alert({ title: TITLE, message: messageFor(item) });
                    return;
                }

                window.alert(TITLE + '. ' + messageFor(item));
            }
        };
    };
})();
